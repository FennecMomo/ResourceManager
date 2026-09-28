# 无干扰的本机控制通道（0.4.5）

客户端提供命名管道 `FennecMomo.ResourceManager.Control.v1.<PID>`，以 `CurrentUserOnly` 限制为当前 Windows 用户。不新增 TCP/HTTP 监听端口，不需要截图、鼠标、键盘或激活窗口。反馈服务端不在此通道的控制范围内。

## 命令行

在仓库根目录的 PowerShell 中运行：

```powershell
# 只读状态；旧版会返回 control_unavailable，不会唤醒窗口
./tools/client-control.ps1 -Action Status

# 正常退出指定客户端，保存反馈草稿并停止共享、下载等服务
./tools/client-control.ps1 -Action Shutdown -ClientProcessId 12345

# 等旧进程正常退出，再后台启动新版并核对路径、版本、共享服务状态
./tools/client-control.ps1 -Action Restart -ClientProcessId 12345 `
  -ExecutablePath 'E:\etc\ResourceManager\dist\win-x64-0.4.5\ResourceManager.exe'
```

多实例时必须指定 PID。没有运行中的客户端时，`Restart` 可以直接后台启动指定版本。仅查询状态不会停止进程、打开主窗口或修改设置。

`shutdown` 遇到未保存设置或正在准备私发副本会返回拒绝原因，不显示确认弹窗、不丢弃编辑、不强杀。其他下载沿用客户端正常退出时的暂停和保存逻辑。启动返回成功需要从新进程读回匹配的 EXE 路径、版本和 `ready: true`，不能仅凭启动命令成功判断。

旧版 EXE 在运行时无法凭新文件获得管道能力。脚本发现旧版没有通道会保留它并明确报错；需要先经过一次正常退出和新版启动，此后的切换即可使用通道。不能为迁就旧版退回桌面自动化或强制终止。

## 后台启动

`ResourceManager.exe --background` 使用正常 Windows 桌面数据环境、从托盘启动，不激活已运行的实例。存储尚未初始化、存在待处理迁移或资料验证失败时，以非零退出码结束并记录日志，不弹出存储选择窗口。首次选择目录及迁移恢复仍由用户明确打开的交互流程负责。

后台启动只改变启动和管理方式；应用正常运行期间由用户启用的聊天通知等功能保持原行为。

## 协议

使用 UTF-8（无 BOM）、单行 JSON 请求和响应，一次连接一个请求：

```json
{"protocol":1,"command":"status","processId":12345}
```

只接受 `status`、`shutdown`。版本和目标 PID 必须匹配；请求最多 4096 字符，连接读取和应答有 5 秒期限。没有激活窗口、任意执行脚本或任意文件读写命令。

响应字段：`success`、`error`、`data`。状态包括实际 EXE、版本、数据目录、共享服务状态、窗口是否可见、当前页面、未保存设置、私发副本准备状态、活动下载数、公开资源数及设备数。它不返回聊天正文、文件内容、令牌或密钥。

0.4.8 增加 `unreadChatMessages`（总未读数，包含静音会话）和 `chatTrayFlashing`（是否存在触发托盘闪烁的非静音未读）。均为只读汇总，不会改变已读状态或触发提醒。

退出确认先写回管道，再进入应用既有的 `ExitAsync`。`unsaved_settings`、`preparing_private_resource`、`unsupported_command`、`invalid_protocol_or_process`、`invalid_request`、`handler_failed` 都是机器可读失败原因。

## 验证

协议回归覆盖目标校验、命令白名单、大小限制、非法 JSON、空闲连接超时、异常恢复、拒绝退出和退出应答顺序。隔离 WPF 验证使用真实 `MainWindow` 控制处理器，全程不调用 `Show()`：查询返回 `windowVisible: false`；修改隔离昵称后拒绝退出；还原后正常退出。测试不启动发现服务，不接触用户资料，也不抢焦点。
