# 公网服务器连接与部署（客户端 0.6.1 / 服务端 0.1.0）

客户端在“服务器 → 添加服务器”填名称和 HTTPS 根地址即可加入，无需邀请码、账号或人工审批。首次加入自动登记固定设备 ID 和公钥，后续登记必须证明持有同一私钥。服务器签发短期会话令牌；设备私钥随本机数据保留，令牌仅留在内存，重启重新登记。绑定保存服务器 ID、公钥、地址、显示名称、连接状态和最近名单，不与局域网设备地址簿混用。

本阶段支持多台服务器、设备名单、在线状态、资料同步和自动重连。资源目录、资源数量、跨网下载/中继与服务器上传留在 0.6.2–0.6.4；添加服务器不会上传本机文件。旧版 0.0.1 反馈服务不具备 `workspace-v1`，需要单独部署 0.1.0 才能加入。

## 连接规则

- 仅接受 HTTPS 根地址，例如 `https://files.example.com`。证书按操作系统信任链验证；不跳过验证，不跟随重定向。HTTP 仅允许 localhost/回环地址做本机测试。
- 第一次通过有效 HTTPS 自动信任服务器签名公钥；后续 ID 或公钥改变会停止重连。更换域名可以编辑原绑定，但必须仍是原服务器。服务器丢失数据库后不要用空目录冒充原服务器。
- 在线名单使用最长 20 秒的 HTTP 长轮询，变化时立即返回；50 秒无心跳后判离线，每 5 秒检查一次。反向代理读超时须至少 35 秒。
- 同一设备的新会话替换旧会话；旧客户端收到 409 后停止自动重连，并显示“连接受限”。设备被管理员停用返回 403，同样停止。恢复后点击“重新连接”。
- 会话有效期滑动续期 30 分钟；过期会自动重新登记。断线按 1、2、4、8、16、30 秒退避，保留缓存名单并明确标为待核对。
- 服务重启保留身份与登记资料，在线状态重新确认。移除绑定不删除本机发布资源或局域网设备记录。更换本机存储目录会迁移绑定、名单缓存与设备密钥。
- 默认最多登记 256 台设备，可配置到 1000。每来源每分钟最多 30 次登记；反向代理后的客户端共同计入代理来源，集中重启时可能短暂退避。名单头像最多 32 KiB，过大的头像使用默认图标。

## 配置和命令

覆盖顺序：默认值 < JSON 文件 < `RM_*` 环境变量 < 命令行。JSON 使用连字符键，环境变量使用大写下划线，例如 `server-name`、`RM_SERVER_NAME`、`--server-name`。未知选项、无效端口、无效容量直接失败。

默认配置：Windows 为 EXE 同目录 `server.json`；Linux 为 `/etc/resourcemanager/server.json`。可用 `RM_CONFIG` 或 `--config FILE` 指定。默认数据目录：Windows 为当前用户 LocalAppData 下 `ResourceManager.Server`；Linux 为 `/var/lib/resourcemanager`。

| 配置 | 默认值 | 含义 |
| --- | --- | --- |
| `data-dir` | 见上文 | 数据库、密钥、反馈附件 |
| `server-name` | 主机名 | 服务显示名称 |
| `api-address` / `api-port` | `0.0.0.0` / `37644` | API 监听；公网部署用反向代理 |
| `admin-port` | `37646` | 仅回环访问的管理端口 |
| `discovery-enabled` / `discovery-port` | `true` / `37645` | 局域网 UDP 反馈发现；公网部署关闭 |
| `max-devices` | `256` | 累计登记设备上限，包括被停用设备 |
| `upload-dir` | 数据目录下 `uploads` | 后续上传存储的预留配置 |
| `max-capacity-bytes` | `107374182400` | 后续上传总容量的预留配置 |
| `max-file-bytes` | `1073741824` | 后续单文件限制的预留配置 |

上传与配额尚未实现，后三项目前只校验并保留配置，不限制已有反馈附件；反馈仍使用其独立限制。

```bash
./ResourceManager.Server --help
./ResourceManager.Server --version
./ResourceManager.Server check-config --config /etc/resourcemanager/server.json
./ResourceManager.Server doctor --config /etc/resourcemanager/server.json
# 仅在服务已停止时运行：
./ResourceManager.Server migrate --config /etc/resourcemanager/server.json
```

`check-config` 只校验配置；`doctor` 校验数据目录可写性（必要时创建目录，再创建并删除探针文件）；`migrate` 幂等初始化/迁移数据库。诊断命令输出 JSON，退出码 0 为成功、2 为配置错误、3 为存储错误。正常服务日志写 stdout/stderr，交给 journald 或容器收集；启动异常由宿主报告非零退出码。

## Linux systemd 部署

使用带 glibc 的 x64 Linux，安装 .NET 10 所需系统原生依赖；包已包含 .NET 运行时，不需要另装 SDK。解压 `ResourceManager.Server-linux-x64.tar.gz` 后：

```bash
chmod +x ./ResourceManager.Server
sudo bash ./deploy/install.sh "$PWD"
sudo systemctl status resourcemanager
sudo journalctl -u resourcemanager -n 100 --no-pager
curl --fail http://127.0.0.1:37644/health/ready
```

脚本创建专用非登录用户，安装到 `/opt/resourcemanager`，数据放 `/var/lib/resourcemanager`。再次执行会先停止该 systemd 服务并备份程序、配置和完整数据到 `/var/backups/resourcemanager/时间-进程号`，保留已有配置，再校验、迁移并启动。失败时停止后续步骤、报告备份位置，不自动拿旧程序打开新库。

脚本针对默认路径；自定义数据目录时先自行备份对应路径，并调整 unit 的 `ReadWritePaths`。备份包含身份私钥，目录权限应保持 0700。回滚时停止服务，同时恢复匹配的程序、配置和数据副本，修正数据所有权，再启动；不能只复制运行中的 SQLite 主文件。

公网域名解析到服务器，开放 80/443，使用 `deploy/Caddyfile`：给 Caddy 服务设置 `RM_DOMAIN=files.example.com`，默认上游 `127.0.0.1:37644`。该模板仅代理 `/api/v1/workspace/*`，其余路径返回 404，管理页和原局域网反馈接口不会通过此域名公开。Caddy 的域名站点会管理 HTTPS 证书，环境变量占位语法见 [官方 Caddyfile 文档](https://caddyserver.com/docs/caddyfile/concepts)。

## Docker Compose（源码构建）

在完整源码仓库中执行（发布包不包含完整源码）：

```bash
RM_DOMAIN=files.example.com docker compose -f deploy/server/compose.yml up -d --build
docker compose -f deploy/server/compose.yml logs --tail=100 server
```

模板使用 .NET 10 SDK 构建、ASP.NET 10 非 root 运行环境，方式参考 [.NET 官方容器文档](https://learn.microsoft.com/en-us/dotnet/core/docker/build-container)。只公布 Caddy 的 80/443；服务端 API 留在容器网络内。数据、证书使用独立命名卷。更新前停止 server 容器并备份完整 server-data 卷，重新构建后启动；不要使用 `down -v` 清空持久数据。容器管理 API 从 server 容器内部或受保护的同命名空间维护工具调用，不把管理端口映射到公网。

## Windows 服务端

解压 Windows 服务端包，用配置文件或 CLI 启动：

```powershell
.\ResourceManager.Server.exe --data-dir 'D:\ResourceManagerServer' --api-address 127.0.0.1 --discovery-enabled false
```

公网同样通过 HTTPS 反向代理；现有局域网反馈部署继续使用原参数即可。更新须先正常停止原服务端并备份完整数据目录，再替换程序。客户端更新不自动停止独立反馈服务。

## 本机管理和诊断

`GET http://127.0.0.1:37646/admin/api/workspace/members` 返回已登记设备（包括 `blocked` 状态），不返回令牌或密钥。通过 `PATCH /admin/api/workspace/members/{deviceId}/blocked` 和 `{"blocked":true}` 停用设备；`false` 恢复。此项目前是管理 API，尚未增加浏览器按钮。身份不同的设备可直接登记，这是本次确定的开放加入方式。

检查 `GET /api/v1/workspace/capabilities` 的 `protocol=workspace-v1`。若域名根路径返回 404 是模板的预期行为。客户端提示连接受限时检查是否有相同设备资料的另一个客户端运行、是否被管理员停用、是否更换了服务器数据目录。未经身份确认不覆盖原绑定。
