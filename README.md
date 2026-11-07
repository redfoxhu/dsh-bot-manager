# DSH 机器人管家

一个 Windows 托盘程序，用界面管理本机的 **AstrBot + SnowLuma + DSH**：

- 一键隐藏启动 AstrBot 和 SnowLuma（不显示控制台窗口，不占任务栏）。
- 安装并管理 AstrBot 侧的「DSH 连接控制」插件。
- 管理员在 QQ 里发送命令，连接/断开 DSH 桥；断开后普通聊天不受影响，DSH 任务命令会被拦截。
- 可选开机自启；关闭窗口时可选「隐藏到托盘」或「退出并停止 AstrBot/SnowLuma」。
- 提供状态、设置、日志、关于四个页面。

程序只做“管理”和“转接开关”，不重复实现 AstrBot 的 QQ 接入，也不接管 DSH 的模型调用。真正的 DSH 任务是 AstrBot 的 `astrbot_plugin_dsh` 桥插件转给 DSH 的 `dsh-astrbot-ingress`。

## 环境要求

- Windows 10/11。
- .NET Framework 4.8（Windows 10/11 通常自带）。
- 已安装并配置好：
  - AstrBot（启动器实例目录，例如 `C:\Users\<你>\.astrbot_launcher\instances\<id>`）。
  - SnowLuma（默认 `E:\SnowLuma`）。
  - DSH（默认 `E:\dsh`）。
- AstrBot 已安装 `astrbot_plugin_dsh` 桥插件，并且 QQ 平台已能正常收发消息。
- 管理员 QQ 已在 AstrBot 的 `admins_id` 中（默认预置 `1031998556`，可在本程序设置里改）。

## 文件说明

```
dist\DshBotManager.exe          编译产物（不要直接从工作区运行，见下）
install.cmd                     双击安装到当前用户目录并创建桌面快捷方式
install.ps1                     安装脚本（复制 exe、设为 Medium 完整性、建快捷方式）
src\                            C# 源码
payload\astrbot_plugin_dsh_control\
                                AstrBot 控制插件源文件（会嵌入 exe）
build.ps1                       使用系统自带 csc.exe 重新编译
README.md                       本说明
```

> 重要：`E:\dsh\app\bot自启动` 是 DSH 的工作区，里面的文件带 **Low 完整性标签**。  
> 即使从桌面双击 `dist\DshBotManager.exe`，进程也可能仍是低完整性，无法写
> `%LOCALAPPDATA%` 和 AstrBot 配置。  
> **请先双击 `install.cmd`**，安装到
> `%LOCALAPPDATA%\Programs\DshBotManager`，以后从桌面快捷方式启动。

配置和日志默认在：

```
%LOCALAPPDATA%\DshBotManager\config.json
%LOCALAPPDATA%\DshBotManager\logs\manager.log
```

## 首次使用

1. **从资源管理器/桌面启动 `DshBotManager.exe`**。首次请先双击工作区里的 `install.cmd`，把程序安装到 `%LOCALAPPDATA%\Programs\DshBotManager`，再使用桌面上的“DSH机器人管家”快捷方式。不要从 DSH 的聊天、文件卡片或沙箱终端启动。
2. 打开「设置」页，确认：
   - AstrBot 实例目录：留空会自动扫描 `%USERPROFILE%\.astrbot_launcher\instances`，选中带桥配置的实例；也可以手动选择。
   - SnowLuma 目录、DSH 目录。
   - 三条聊天命令（默认 `连接dsh`、`断开dsh`、`dsh状态`）。
   - 管理员 QQ。
   - AstrBot 端口（默认 `6199`）、SnowLuma 端口（默认 `5099`）、DSH ingress 端口（默认 `3188`）。
   - 关闭行为、是否开机自启、启动时是否自动拉起服务。
3. 点「保存设置」。
4. 如果 DSH 侧还没有 ingress：点「安装/修复 DSH 桥」。程序会调用
   `dsh plugin --profile desktop add dsh-astrbot-ingress`，并把包加入
   `dsh.profile.bundles`。**安装后需要手动重启一次 DSH**，新的 bundle 才会加载。
5. 点「一键启动」。程序会：
   - 释放并安装控制插件到 AstrBot 的 `data\plugins\astrbot_plugin_dsh_control`；
   - 写控制 runtime；
   - 需要时启动 DSH；
   - 隐藏启动 AstrBot（venv Python + `core\main.py`，`cwd=core`，`ASTRBOT_ROOT=core`）；
   - 隐藏启动 SnowLuma（`node.exe index.mjs`，并设置 `SNOWLUMA_HOOK_AUTOLOAD=1`）。
   如果 SnowLuma 已经在运行，程序会先停掉旧实例再重启，确保它重新注入当前 QQ。
6. 如果 AstrBot 原本已经在运行，而控制插件是刚安装/刚更新的，程序会**自动重启一次 AstrBot** 让插件加载。也可以在界面点「重启 AstrBot」。
7. 在 QQ 里用管理员账号发送命令：

```
连接dsh
断开dsh
dsh状态
/dsh 帮我检查一下这个项目
```

连接后，`/dsh <任务>` 会走 AstrBot 桥 → DSH ingress → DSH。断开后，`/dsh` 会被控制插件拦截并提示先连接，不会落到普通 LLM。

## 关于 DSH 重启

`dsh-astrbot-ingress` 是运行在 DSH 进程内的 bundle。安装/更新后：

- 如果 DSH 正在运行，**必须重启 DSH** 才会监听 `127.0.0.1:3188` 并写
  `%USERPROFILE%\.dsh\astrbot-ingress.json`。
- 界面上的「重启 DSH」会结束所有 `DeepSeek Harness.exe` 进程，**当前 DSH 会话会中断**；程序会二次确认，不会自动执行。
- 如果 DSH 没重启，AstrBot 控制插件仍能连接/断开桥，但真正发 `/dsh` 时会提示找不到 ingress。

## 开机自启与关闭行为

- 勾选「开机自动启动」会写当前用户的
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值为
  `"<程序路径>" --autostart`，不需要管理员权限。
- `--autostart` 启动时隐藏到托盘，并按「启动时自动拉起服务」决定是否自动执行一键启动。
- 「关闭时隐藏到托盘」：点窗口关闭按钮只隐藏，程序继续运行；从托盘菜单「退出」才会退出。
- 「关闭时退出」：点关闭按钮会确认，然后停止 AstrBot 和 SnowLuma，再退出管理器。
- **退出不会停止 DSH**，避免误杀正在干活的 DSH。

## 常见问题

**控制插件显示“未加载/已过期”**

- AstrBot 没重启，或插件文件刚更新。点「重启 AstrBot」。
- 看日志：`%LOCALAPPDATA%\DshBotManager\logs\manager.log` 和 AstrBot 控制台日志。
- 确认插件目录存在：
  `<实例>\core\data\plugins\astrbot_plugin_dsh_control\main.py`。

**Ingress 信标“未发现”**

- DSH 没重启，或 `dsh-astrbot-ingress` 没装进当前 desktop profile。
- 检查 `%USERPROFILE%\.dsh\astrbot-ingress.json` 是否存在且较新。
- 检查 `127.0.0.1:3188` 是否监听。
- 点「安装/修复 DSH 桥」后手动重启 DSH。

**QQ 发命令没反应**

- 发送者必须是 AstrBot 管理员（`admins_id`）或本程序设置里的管理员 QQ。
- 命令要和设置里完全一致（大小写不敏感，允许一个前导 `/`）。
- 群聊默认只允许管理员；私聊需要桥插件的 `private_passthrough` 或 `/dsh` 命令。
- 看 AstrBot 日志里是否出现 `DSH 连接控制插件已启动`。

**AstrBot 启动失败**

- 检查实例目录下 `venv\Scripts\python.exe` 和 `core\main.py` 是否存在。
- 端口 6199 是否被其他程序占用。
- 看管理器日志里的 `[AstrBot]` 输出。

**SnowLuma 启动失败**

- 检查 `E:\SnowLuma\node.exe` 和 `index.mjs`。
- 端口 5099 是否被占用；SnowLuma 的账号是否仍处于登录状态。
- 首次启动如果 QQ 需要重新扫码，请先手动打开一次 SnowLuma 完成登录；本程序只负责后台启动，不代替扫码。

**管理器全绿，但 QQ 机器人不回复**

- “全绿”只表示端口/进程就绪，不代表 SnowLuma 已经注入 QQ。正常日志里应出现：
  `[Hook] login detected`、`[OneBot] session started`、`WS-Client ... connected ws://127.0.0.1:6199/ws`，
  随后 AstrBot 日志出现 `aiocqhttp(OneBot v11) 适配器已连接`。
- 如果只有 `WebUI listening`，没有 `login detected`：QQ 客户端可能在登录页，或 SnowLuma 没有自动注入 Hook。
  本程序启动 SnowLuma 时会设置 `SNOWLUMA_HOOK_AUTOLOAD=1`；如果 SnowLuma 已在运行，会先重启它。
- 如果 QQ 中途重启过，点一次「停止 AstrBot/SnowLuma」，再点「一键启动」，让 SnowLuma 重新注入。
- `AstrBot 面板` 和 `SnowLuma WebUI` 是各自的管理后台登录页，和 QQ 是否登录是两回事：
  - AstrBot 面板要输入 AstrBot dashboard 的用户名/密码；
  - SnowLuma WebUI 要输入 SnowLuma 控制台密码（`E:\SnowLuma\config\webui.json` 里保存的是哈希，不是明文）。
  即使这两个后台没登录，只要 SnowLuma 注入成功，QQ 机器人仍能工作。

**自动启动服务失败：对路径 `...\core\data\config\dsh_control_runtime.json.tmp-...` 的访问被拒绝**

- 这是 DSH 文件沙箱的**低完整性（Low Mandatory Level）**限制，不是 AstrBot 目录的 NTFS 权限问题。即使 `icacls` 显示当前用户有 FullControl，低完整性进程也不能写工作区外的中等完整性文件。
- 典型原因有两个：
  1. 从 DSH 的文件卡片、聊天附件、DSH 终端或 DSH 内部启动器打开；
  2. **直接运行了工作区 `dist\DshBotManager.exe`**——这个文件本身带 Low 完整性标签，即使从桌面双击，进程也可能是低完整性。
- 解决：先双击工作区里的 `install.cmd`，安装到 `%LOCALAPPDATA%\Programs\DshBotManager`；以后从桌面“DSH机器人管家”快捷方式启动。安装脚本会把 exe 设为 Medium 完整性。
- 新版程序会在启动服务前先做一次写权限探测，遇到这种情况会直接提示“请从资源管理器/桌面直接双击”，而不是抛出原始路径异常。
- 如果 Windows SmartScreen 提示“已保护你的电脑”，点「更多信息」→「仍要运行」；这是本地编译的未签名程序，不是病毒。
- **重启后又从 `dist` 路径弹出同样的错误**：说明开机自启项仍指向工作区副本。运行一次 `install.cmd`（从资源管理器双击）会把 HKCU Run 项改到安装版；也可以在安装版设置里把“开机自启”关掉再打开一次。

## 重新编译

需要 Windows 自带的 .NET Framework 4.x 编译器：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

输出：`dist\DshBotManager.exe`。

## 安全说明

- 程序不会读取或记录 DSH ingress 的 token；只读取信标里的 URL。
- DSH 桥的连接/断开只对管理员生效。
- 管理员 QQ、命令、路径都保存在本机 `config.json`，不对外发送。
- 「重启 DSH」是破坏性操作，必须二次确认。

## 卸载

1. 在设置里取消「开机自动启动」并保存。
2. 托盘菜单退出（或在关闭行为为“退出”时关闭窗口）。
3. 可选删除 `%LOCALAPPDATA%\DshBotManager`。
4. 可选删除 AstrBot 的
   `<实例>\core\data\plugins\astrbot_plugin_dsh_control` 目录。
5. 可选移除 DSH desktop profile 里的 `dsh-astrbot-ingress` 依赖/bundle。
