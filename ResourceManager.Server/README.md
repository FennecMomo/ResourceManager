# ResourceManager.Server 0.0.1

局域网反馈服务端。服务端与 ResourceManager 客户端独立版本，兼容性通过 `feedback-v1` 协议判断。

## 启动

```powershell
.\ResourceManager.Server.exe --server-name "办公室反馈服务" --data-dir "D:\ResourceManagerServer"
```

默认端口：

- TCP `37644`：客户端直接提交、查询和关闭反馈。
- UDP `37645`：局域网自动发现。
- TCP `127.0.0.1:37646`：本机管理页。

启动后在服务端本机访问 `http://127.0.0.1:37646/admin/`。管理页不设账号，也不会监听局域网地址；远程维护请先使用远程桌面或受保护的端口转发。

可用参数：

```text
--data-dir PATH
--server-name NAME
--api-port PORT
--discovery-port PORT
--admin-port PORT
--version
--help
```

## 客户端发现与提交

客户端在反馈页点击“刷新”后，会优先检查本机 `127.0.0.1:37644`，再通过 UDP 广播发现局域网服务端。发现后服务端直接出现在投送目标下拉菜单第一项，不需要配对码或额外授权。首版只使用发现到的一台服务端，不处理多服务端选择。

## Issue 管理

管理页默认显示“处理中”的本地反馈和 GitHub Issue，可按来源、分类、状态以及标题或正文筛选。分类和状态使用独立色彩标识；本地反馈的待接受、接受、拒绝和完成按钮也采用不同的语义色。拒绝本地反馈时必须填写原因，拒绝原因会持久化；拒绝或完成后条目从默认处理列表归档，仍可切换到对应状态查看。

## GitHub 授权

GitHub 集成不使用共享 PAT、机器人 Token、Client Secret 或 GitHub App 私钥。需要先注册一个启用 Device Flow 的 GitHub App，只授予 `Metadata: read` 和 `Issues: read/write`，并把公开 Client ID 配置为环境变量：

```powershell
$env:ResourceManager_GitHubClientId = "Iv1.xxxxxxxxxxxxxxxx"
.\ResourceManager.Server.exe
```

维护者在本机管理页使用自己的 GitHub 账号完成设备授权，再填写一个已经安装该 GitHub App 的仓库。服务端每五分钟同步一次，也可以手动刷新。

客户端可以在运行环境使用同一环境变量，正式发布也可以在构建时传入
`-p:ResourceManagerGitHubClientId=...`；仓库发布工作流读取变量 `RESOURCE_MANAGER_GITHUB_CLIENT_ID`。
Client ID 是公开应用标识，不是写入密钥，也不会授予脱离用户账号的共享写入能力。

## 安全边界

首版按需求使用可信局域网 HTTP，不提供传输加密。任何能监听同一网络的人都可能看到反馈正文、附件或随机客户端标识，因此：

- 不要把 API 端口映射到公网。
- Windows 防火墙只允许专用网络访问 TCP `37644` 和 UDP `37645`。
- 不要在不可信 Wi-Fi 上提交敏感资料。
- 日志不会记录反馈正文或附件内容。

附件使用白名单、数量和大小限制，服务端只保存并下载，不执行或解压附件。用户关闭反馈时只把状态标记为 `UserClosed`，不会物理删除后端记录。
客户端每分钟最多提交一条反馈，服务端全局每小时最多接收一百条。客户端随机标识不是授权凭据，只用于区分各自的状态查询和关闭操作；同一局域网内任何人都可以创建新反馈。
