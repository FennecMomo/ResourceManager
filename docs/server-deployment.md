# 公网服务器连接与部署（客户端 0.6.4 / 服务端 0.3.0）

客户端在“服务器 → 添加服务器”填名称和 HTTPS 根地址即可加入，无需邀请码、账号或人工审批。首次加入自动登记固定设备 ID 和公钥，后续登记必须证明持有同一私钥。服务器签发短期会话令牌；设备私钥随本机数据保留，令牌仅留在内存，重启重新登记。绑定保存服务器 ID、公钥、地址、显示名称、连接状态和最近名单，不与局域网设备地址簿混用。

本阶段支持多服务器、在线名单、本机资源发布、直连/中继下载、上传持久存储、离线下载、服务器收藏和客户端更新来源。本机发布仍需发布者在线；上传完成后可由服务器独立提供下载。0.0.1 反馈服务不支持工作空间；0.1.0 支持名单但不支持资源，资源功能需要服务端 0.2.0。服务端通过 `features` 中的 `published-resources-v1` 声明资源能力，客户端会提示旧版升级；上传能力由服务端 0.3.0 的 `stored-resources-v1` 声明。

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
| `upload-dir` | 数据目录下 `uploads` | 已上传文件及未完成上传的持久目录 |
| `max-capacity-bytes` | `0` | 兼容旧配置，当前不执行容量限制 |
| `max-file-bytes` | `0` | 兼容旧配置，当前不执行大小限制 |

按用户确认，上传不设单用户配额、单资源大小上限、固定容量预留或保留期限，也不自动清理。旧配置中的两个容量字段继续接受但不执行；反馈附件沿用原独立限制。分片每次最多 256 KiB 是传输协议边界，不是资源大小限制。

```bash
./ResourceManager.Server --help
./ResourceManager.Server --version
./ResourceManager.Server check-config --config /etc/resourcemanager/server.json
./ResourceManager.Server doctor --config /etc/resourcemanager/server.json
# 仅在服务已停止时运行：
./ResourceManager.Server migrate --config /etc/resourcemanager/server.json
```

`check-config` 只校验配置；`doctor` 校验数据和上传目录可写性（必要时创建目录，再创建并删除探针文件）；`migrate` 幂等初始化/迁移数据库。诊断命令输出 JSON，退出码 0 为成功、2 为配置错误、3 为存储错误。正常服务日志使用 JSON 写 stdout/stderr，交给 journald 或容器收集；就绪输出 `SERVER_READY`，宿主启动/运行失败输出 `SERVER_START_OR_RUN_FAILED` 并返回非零退出码。

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

脚本读取现有 JSON 配置中的数据、上传目录，外置上传目录也纳入备份；自定义目录须预先配置权限，并调整 unit 的 `ReadWritePaths`。备份包含身份私钥，目录权限应保持 0700。回滚时停止服务，同时恢复匹配的程序、配置和数据副本，修正数据所有权，再启动；不能只复制运行中的 SQLite 主文件。

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


## 发布、下载与更新（0.6.2）

在“我的发布”选择文件资源、文件夹资源或分组，点击“发布到服务器…”。勾选多个已绑定服务器即可同步发布；取消勾选撤销。分组选择包含其当前和后续新增资源、子分组，单个资源显式选择优先于分组。文件夹内的子文件须先作为独立资源发布，才可单独设置服务器范围。

目录约每 3 秒检查变化，仅变化时推送；服务器保留资源名称、分组、备注、权限及更新元数据，不保存磁盘路径和文件正文。每设备目录最多 2000 个发布项/2000 个分组，目录请求上限 1 MiB。文件夹清单响应上限 6 MiB；超限时需拆分发布。空目录和文件夹结构保留。

选择服务器中的设备和资源后下载。已有设备端点（包括手动连接、路由器映射）按直连处理；没有端点时做局域网发现，再通过签名握手核对设备 ID 和已登记公钥。发现并握手成功走直连，无局域网发现结果且无已有连接才走中继。已有直连失败或局域网身份验证失败直接报告，不降级中继；换网络后已失效的旧设备连接需用户更新或移除，程序不会擅自绕过它。

直连和中继使用相同的签名资源请求、当前发布选择及分组权限检查。服务器设备可在分组白名单中选择，不要求先加入局域网设备地址簿。服务器目录过滤不可见资源和分组；中继入口检查权限，发布端再次检查当前配置，防止目录同步延迟导致撤销失效。新请求不能绕过权限，已下载的文件不会被收回。

中继使用发布端主动发起的反向长轮询：下载者向服务器提交签名请求，发布端领取并返回数据。每片最多 256 KiB，服务器全局最多 128 个待处理请求、单发布者最多 8 个；普通请求等待最多 25 秒，首次计算完整文件哈希的元数据请求最多 5 分钟，忙碌或离线会明确中断。反向代理如设置读超时，应为文件元数据请求保留至少 6 分钟。JSON 分片会增加约三分之一传输体积，此版本尚未验证真实公网高并发吞吐。数据只短暂经过内存，不写临时文件。传输中断保留 `.rm-part` 和 `.rm-etag`，继续时以内容哈希确认原文件，完成后再核对 SHA-256，损坏分片删除后可重试。

启动和手动“检查更新”都会查询 GitHub、局域网设备和所有绑定服务器。只使用有权限且可下载的安装包候选；本机发布要求发布者在线，服务器存储不要求上传者在线；按版本及哈希合并重复项，明确来源服务器和发布者。无法查询的服务器单独提示，不把部分检查成功说成全部已是最新版。下载使用相同路由规则，验证大小、SHA-256、内嵌版本和 Windows x64 文件头；同版本官方包摘要不匹配的共享源不会被使用，未获官方确认的版本仍需用户手动信任。服务器根地址可加入并不授予静默安装权限。

客户端下载任务保存服务器绑定和发布者身份，重启后可继续；移除服务器后任务会提示原服务器已移除，不会误用另一服务器同名资源。移除服务器会通知本机离线，服务器保留此前目录，直到重新连接后更新；本机发布文件不会被删除。


## 上传、收藏与删除（客户端 0.6.4 / 服务端 0.3.0）

服务器页选择目标服务器，点击“上传…”选择文件或文件夹，然后设置公开、私有或设备白名单。支持多选文件。上传不会改变本机源文件；同名资源分别保存，不静默覆盖。文件夹保留空目录与层级，排除 `.git`，拒绝符号链接、重解析点、越界路径及不能跨 Windows/Linux 使用的文件名。

“上传任务”显示准备、进度、暂停、继续、错误和删除操作。准备阶段计算每个文件的 SHA-256；每个文件通过校验后才允许整个资源发布。重启后未完成任务保持暂停，用户点击继续时重新核对源文件并读取服务器进度。源文件已改变时提示新建上传任务；坏分片导致的哈希失败会清空该文件临时内容后允许重传。未完成内容保存在 `uploads/pending`，完成后同文件系统原子移动到 `uploads/ready`，再提交资源目录。目录已移动而数据库尚未提交的意外中断可通过继续任务恢复。不要只迁移数据库或单独清理这些目录。

上传完成后资源在所有者的“服务器存储”分组下显示，所有者离线也可下载。下载始终取自服务器存储；本机发布资源继续遵循既有直连优先规则。已上传的 `ResourceManager.exe` 进入更新候选，下载后仍校验实际版本、平台和完整性，不能仅凭声明安装。

“收藏”保存服务器固定 ID、所有者设备 ID、资源 ID 和存储模式；同名资源及不同服务器互不混淆。目录撤销或权限移除后收藏显示不可访问，保留记录供用户取消收藏；下载列表标明设备直连、服务器发布者或服务器存储。移除绑定后不会把任务转给其他服务器。

所有者可以修改已上传资源权限或删除副本；删除任务也会删除对应临时文件或已发布副本，需要明确确认。先隐藏目录再删除文件，正在进行的下载在下一次分片请求时停止。删除过程中磁盘操作失败会保留可重试状态，不把失败当作成功。本机源文件、其他设备已经下载的文件不受影响。

## 审计与管理员资源管理

审计存于 `workspace.db` 的 `audit` 表，记录设备登记/退出/停用、目录发布与权限同步、上传开始/校验异常/完成、删除、权限变更及服务器拒绝的下载请求。只记录时间、动作、设备 ID、资源 ID 和结果码，不记录正文、令牌或密钥。审计写入失败输出 `AUDIT_WRITE_FAILED`；磁盘读写错误接口返回 HTTP 507 和 `STORAGE_IO_ERROR`。直连内容请求发生在发布者电脑，不属于服务器可观测流量。

仅服务器本机管理端口可访问：

- `GET /admin/api/workspace/audit?before=序号`：每次最多 200 条，按序号向前翻页。
- `GET /admin/api/workspace/resources`：包含已发布、上传中和删除待重试资源，提供资源 ID 与所有者。
- `DELETE /admin/api/workspace/resources/{id}`：管理员删除对应资源或未完成上传，规则与所有者删除相同。

这些管理能力通过 API 提供，不增加浏览器交互流程。公网反向代理不开放 `/admin`。

## 可重复备份、恢复、升级与卸载

Linux 安装脚本需要 `python3`、`curl`、systemd 及现有 .NET 原生依赖。脚本无交互，已有配置和密钥不覆盖。配置及任何秘密放入受权限保护的文件或服务环境，不放入普通日志和命令行参数。升级继续执行 `install.sh 新包目录`，先停止服务并备份，然后校验配置、检查目录、迁移、启动并等待健康接口。

```bash
sudo bash /opt/resourcemanager/deploy/maintain.sh backup /var/backups/resourcemanager/manual-20260928
sudo bash /opt/resourcemanager/deploy/maintain.sh restore /var/backups/resourcemanager/manual-20260928
# 仅注销服务，保留程序、配置和全部数据
sudo bash /opt/resourcemanager/deploy/uninstall.sh
```

维护脚本先正常停止服务，完成后按先前运行状态恢复；失败时保留停止状态并输出失败步骤，避免继续操作。备份目录须为新目录，不能位于被备份目录中。`maintenance.py` 将程序、配置、数据以及外置上传目录一起复制，并记录文件 SHA-256；恢复前校验整个备份和目标路径，随后保留现有目录为 `.before-restore-*`，恢复对应版本的完整副本与所有权。失败时查阅 JSON 中的 `step`/`code`，保留备份及旧目录，修复错误后再启动；不拿旧程序直接打开新数据库。

Windows 可在正常停止服务后使用 Python 3 调用相同工具，并显式指定 `--config 配置文件 --program 程序目录 --backup 新备份目录`；配置文件应独占一个配置目录，因为备份包含其所在目录。

Docker Compose 保留独立持久卷；升级前用 `docker compose stop server` 停止写入，备份完整数据卷及 Compose/Caddy 配置（上传默认在该数据卷内），再重新构建并启动。恢复时停止 server，将匹配版本的完整数据卷、配置和镜像一起恢复，保留容器运行用户的文件所有权；不能只恢复 SQLite 主文件。管理 API 仍仅容器内回环可用。不要执行 `down -v` 删除数据。

`install.sh` 输出 `INSTALL_READY`、`INSTALL_STEP_FAILED` 或 `HEALTH_TIMEOUT`；维护工具输出 `BACKUP_COMPLETE`、`RESTORE_COMPLETE` 或 `MAINTENANCE_FAILED`；卸载输出 `SERVICE_REMOVED_DATA_PRESERVED`。部署脚本语法和临时目录中的备份恢复逻辑由开发检查覆盖。按用户要求，本次不开展真实公网、目标 Linux、systemd/Docker 联合验收，也不自动替换正在运行的独立反馈服务端。
