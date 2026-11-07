from __future__ import annotations

import asyncio
import json
import os
import time
import uuid
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from astrbot.api import AstrBotConfig, logger
from astrbot.api.event import AstrMessageEvent, filter
from astrbot.api.star import Context, Star, register


BRIDGE_NAME = "astrbot_plugin_dsh"
DEFAULT_CONNECT = "连接dsh"
DEFAULT_DISCONNECT = "断开dsh"
DEFAULT_STATUS = "dsh状态"
DEFAULT_ADMIN = "1031998556"

PLUGIN_DIR = Path(__file__).resolve().parent
DATA_DIR = PLUGIN_DIR.parents[1]
CONFIG_DIR = DATA_DIR / "config"
RUNTIME_PATH = CONFIG_DIR / "dsh_control_runtime.json"
STATUS_PATH = CONFIG_DIR / "dsh_control_status.json"


def _now() -> int:
    return int(time.time())


def _read_json(path: Path) -> Optional[Dict[str, Any]]:
    try:
        if not path.exists():
            return None
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        if isinstance(data, dict):
            return data
    except Exception:
        logger.exception("读取 JSON 失败: %s", path)
    return None


def _atomic_write(path: Path, data: Dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp-" + uuid.uuid4().hex)
    text = json.dumps(data, ensure_ascii=False, indent=2)
    tmp.write_text(text, encoding="utf-8")
    os.replace(str(tmp), str(path))


def _body(text: str) -> str:
    value = (text or "").strip()
    if value.startswith("/"):
        value = value[1:].lstrip()
    return value


def _norm_cmd(value: Any) -> str:
    if value is None:
        return ""
    text = str(value).strip()
    if text.startswith("/"):
        text = text[1:].lstrip()
    return text


def _as_list(value: Any) -> List[str]:
    if isinstance(value, (list, tuple)):
        result: List[str] = []
        for item in value:
            text = str(item).strip()
            if text and text not in result:
                result.append(text)
        return result
    if isinstance(value, str):
        result = []
        for part in value.replace("，", ",").replace(";", ",").replace("；", ",").split(","):
            text = part.strip()
            if text and text not in result:
                result.append(text)
        return result
    return []


def _safe_int(value: Any, default: int = 0) -> int:
    try:
        return int(value)
    except Exception:
        return default


def _as_bool(value: Any, default: bool = True) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return value != 0
    if isinstance(value, str):
        text = value.strip().casefold()
        if text in ("1", "true", "yes", "on", "y"):
            return True
        if text in ("0", "false", "no", "off", "n", ""):
            return False
    return default


@register(
    "astrbot_plugin_dsh_control",
    "local",
    "管理员通过聊天连接/断开 DSH 桥",
    "1.0.0",
)
class DshControlPlugin(Star):
    def __init__(self, context: Context, config: AstrBotConfig = None):
        super().__init__(context)
        self.config = config or {}
        self._task: Optional[asyncio.Task] = None
        self._runtime: Optional[Dict[str, Any]] = None
        self._bridge_error = ""
        self._last_status_write = 0.0

    async def initialize(self) -> None:
        if not RUNTIME_PATH.exists():
            _atomic_write(RUNTIME_PATH, self._make_default_runtime())
        self._runtime = _read_json(RUNTIME_PATH) or self._make_default_runtime()
        self._task = asyncio.create_task(self._loop())
        logger.info("DSH 连接控制插件已启动，runtime=%s", RUNTIME_PATH)

    async def terminate(self) -> None:
        if self._task is not None:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
            except Exception:
                logger.exception("停止 DSH 连接控制轮询任务失败")
            self._task = None

    @filter.event_message_type(filter.EventMessageType.ALL, priority=100)
    async def on_message(self, event: AstrMessageEvent):
        text = event.message_str or ""
        value = _body(text)
        if not value:
            return

        runtime = self._runtime or _read_json(RUNTIME_PATH) or self._make_default_runtime()
        connect = _norm_cmd(runtime.get("connect_cmd", DEFAULT_CONNECT))
        disconnect = _norm_cmd(runtime.get("disconnect_cmd", DEFAULT_DISCONNECT))
        status = _norm_cmd(runtime.get("status_cmd", DEFAULT_STATUS))
        lowered = value.casefold()

        is_connect = bool(connect) and lowered == connect.casefold()
        is_disconnect = bool(disconnect) and lowered == disconnect.casefold()
        is_status = bool(status) and lowered == status.casefold()

        if not (is_connect or is_disconnect or is_status):
            # 断开状态下拦截桥命令，避免落到普通 LLM 后得到误导性回复。
            if not _as_bool(runtime.get("enabled", True)):
                bridge_cmd = _norm_cmd(self._bridge_command())
                if bridge_cmd:
                    prefix = bridge_cmd.casefold() + " "
                    if lowered == bridge_cmd.casefold() or lowered.startswith(prefix):
                        yield event.plain_result(
                            "DSH 未连接，请先发送 " + (connect or DEFAULT_CONNECT) + "。"
                        )
                        event.stop_event()
            return

        if not self._is_admin(event, runtime):
            yield event.plain_result("只有管理员可以切换 DSH 连接。")
            event.stop_event()
            return

        if is_connect:
            yield event.plain_result(self._set_enabled(True, runtime))
            event.stop_event()
            return

        if is_disconnect:
            yield event.plain_result(self._set_enabled(False, runtime))
            event.stop_event()
            return

        yield event.plain_result(self._status_text(runtime))
        event.stop_event()

    def _make_default_runtime(self) -> Dict[str, Any]:
        cfg = self.config if isinstance(self.config, dict) else {}
        return {
            "connect_cmd": _norm_cmd(cfg.get("connect_cmd", DEFAULT_CONNECT)) or DEFAULT_CONNECT,
            "disconnect_cmd": _norm_cmd(cfg.get("disconnect_cmd", DEFAULT_DISCONNECT)) or DEFAULT_DISCONNECT,
            "status_cmd": _norm_cmd(cfg.get("status_cmd", DEFAULT_STATUS)),
            "enabled": self._bridge_enabled(),
            "admin_ids": _as_list(cfg.get("admin_ids", [DEFAULT_ADMIN])) or [DEFAULT_ADMIN],
            "revision": 1,
            "updated_at": _now(),
        }

    def _bridge(self):
        try:
            meta = self.context.get_registered_star(BRIDGE_NAME)
        except Exception:
            meta = None
        if meta is None:
            return None
        return getattr(meta, "star_cls", None)

    def _bridge_config(self) -> Optional[Dict[str, Any]]:
        bridge = self._bridge()
        if bridge is None:
            return None
        cfg = getattr(bridge, "config", None)
        if isinstance(cfg, dict):
            return cfg
        return None

    def _bridge_enabled(self) -> bool:
        cfg = self._bridge_config()
        if cfg is None:
            return True
        try:
            return _as_bool(cfg.get("enabled", True))
        except Exception:
            return True

    def _bridge_command(self) -> str:
        cfg = self._bridge_config()
        if cfg is None:
            return "dsh"
        return _norm_cmd(cfg.get("command", "dsh")) or "dsh"

    def _is_admin(self, event: AstrMessageEvent, runtime: Dict[str, Any]) -> bool:
        try:
            if bool(event.is_admin()):
                return True
        except Exception:
            pass
        try:
            sender = str(event.get_sender_id())
        except Exception:
            sender = ""
        admins = _as_list(runtime.get("admin_ids", [])) or [DEFAULT_ADMIN]
        return sender in admins

    def _apply_bridge_enabled(self, enabled: bool) -> Tuple[bool, str]:
        bridge = self._bridge()
        if bridge is None:
            return False, "桥插件 astrbot_plugin_dsh 未加载"
        cfg = getattr(bridge, "config", None)
        if not isinstance(cfg, dict):
            return False, "桥插件配置不可用"
        try:
            current = _as_bool(cfg.get("enabled", True))
            if current != enabled:
                cfg["enabled"] = enabled
                saver = getattr(cfg, "save_config", None)
                if callable(saver):
                    saver()
            return True, ""
        except Exception as exc:
            logger.exception("写入 DSH 桥 enabled 失败")
            return False, str(exc)

    def _set_enabled(self, enabled: bool, runtime: Dict[str, Any]) -> str:
        runtime["enabled"] = enabled
        runtime["updated_at"] = _now()
        runtime["revision"] = _safe_int(runtime.get("revision"), 0) + 1
        _atomic_write(RUNTIME_PATH, runtime)
        self._runtime = runtime

        ok, error = self._apply_bridge_enabled(enabled)
        self._bridge_error = error
        if enabled:
            text = "DSH 已连接。发送 /" + self._bridge_command() + " <任务> 交给 DSH。"
            if self._read_beacon() is None:
                text += "\n未检测到 DSH ingress 信标，请启动/重启 DSH。"
        else:
            text = "DSH 已断开。普通聊天不受影响。"
        if not ok:
            text += "\n注意：桥插件未加载或配置不可写：" + error
        return text

    def _status_text(self, runtime: Dict[str, Any]) -> str:
        ok, error = self._apply_bridge_enabled(_as_bool(runtime.get("enabled", True)))
        enabled = self._bridge_enabled()
        beacon = self._read_beacon()
        lines = [
            "DSH 桥：" + ("已连接" if enabled else "已断开"),
            "控制插件：运行中",
            "Ingress：" + (("正常 " + str(beacon.get("url", ""))) if beacon else "未发现"),
        ]
        if not ok:
            lines.append("桥插件：未加载或不可写（" + error + "）")
        if error and ok:
            lines.append("最近错误：" + error)
        return "\n".join(lines)

    def _read_beacon(self) -> Optional[Dict[str, Any]]:
        home = os.environ.get("DSH_HOME", "").strip()
        base = Path(home) if home else (Path.home() / ".dsh")
        path = base / "astrbot-ingress.json"
        try:
            if not path.exists():
                return None
            if time.time() - path.stat().st_mtime > 180:
                return None
            data = json.loads(path.read_text(encoding="utf-8-sig"))
            if not isinstance(data, dict):
                return None
            if not str(data.get("url", "")).strip():
                return None
            return {"url": str(data.get("url", "")).strip()}
        except Exception:
            return None

    async def _loop(self) -> None:
        while True:
            try:
                runtime = _read_json(RUNTIME_PATH)
                if runtime is None:
                    runtime = self._make_default_runtime()
                    _atomic_write(RUNTIME_PATH, runtime)
                self._runtime = runtime
                ok, error = self._apply_bridge_enabled(_as_bool(runtime.get("enabled", True)))
                self._bridge_error = error
                self._write_status(ok)
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("DSH 控制轮询失败")
            await asyncio.sleep(1.0)

    def _write_status(self, bridge_ok: bool) -> None:
        now = time.time()
        if now - self._last_status_write < 2.0:
            return
        self._last_status_write = now
        bridge = self._bridge()
        beacon = self._read_beacon()
        data = {
            "plugin": "running",
            "bridge_loaded": bridge is not None,
            "bridge_enabled": self._bridge_enabled(),
            "ingress_beacon": beacon is not None,
            "ingress_url": str(beacon.get("url", "")) if beacon else "",
            "updated_at": _now(),
            "last_error": self._bridge_error if not bridge_ok else "",
        }
        try:
            _atomic_write(STATUS_PATH, data)
        except Exception:
            logger.exception("写入 DSH 控制 status 失败")
