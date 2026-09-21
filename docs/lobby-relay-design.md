# 资源管理器大厅中继设计

> 状态：设计稿 · 目标版本 0.3.0（新模块：大厅中继）· 2026-09-21

## 1. 背景与目标

现状：每台电脑运行一个节点，内嵌 HTTP 服务（默认监听 `37642`），通过 IPv4 地址直连互访，只在能互相路由的网络（局域网）内可用。

目标：新增一台公网服务器 C（大厅），A、B 位于不同网络、都不知道自己的公网 IP，只要各自能出站访问 C，就能在大厅相遇并互传资源。

- A、B 各自向 C 建立长连接；C 维护在线列表。
- 经大厅相遇的设备，全部流量经 C 中继，不做 NAT 打洞、不做直连回退。
- 在线语义：A 眼中 B 在线 ⇔ A 已进入大厅 C 且 B 在 C 的在线列表中。
- 大厅开放：任何知道 C 地址的客户端都能进入，不做鉴权（与现有「能连上端口即可浏览」的安全姿态一致）。
- 保留现有局域网直连模式，两者互不影响。

非目标（本期不做）：

- NAT 打洞与 UDP 传输
- 端到端加密（大厅可见中继明文，仅保证客户端到大厅的传输加密）
- 账号体系、邀请码、设备审批（列为后续可选）
- 多大厅联邦、跨大厅互访

## 2. 术语

| 术语 | 含义 |
| --- | --- |
| 节点 | 运行资源管理器的一台电脑（A、B） |
| 大厅 | 公网中继服务 `ResourceManager.Relay`（C） |
| 控制通道 | 节点到大厅的出站 WebSocket 长连接，连接即代表在线 |
| 转发请求 | A 经大厅发往 B 的 HTTP 请求 |
| 响应回传 | B 执行后经出站 HTTP 流把响应送回大厅、再转给 A 的通道 |
| 直连设备 | 地址簿中带 IPv4 + 端口的设备 |
| 大厅设备 | 地址簿中带大厅地址（`relay_uri`）的设备 |

## 3. 总体架构

```text
        ┌────────────┐        出站 WSS（控制通道）        ┌─────────────────────┐
        │ 节点 A      │ ────────────────────────────────▶ │  大厅 C（VPS）       │
        │ 设备列表     │ ◀──────────────────────────────── │  · 在线列表          │
        └─────┬──────┘        转发请求 / 响应内容           │  · 控制通道注册表     │
              │                                          │  · 请求关联表        │
              │            ┌───────────────────────┐      └──────────┬──────────┘
              └───────────▶│  A 请求 /api/v1/relay/B/... │                 │
                           └───────────────────────┘                 │ 出站 WSS（控制通道）
                                                                     ▼
                                                          ┌─────────────────────┐
                                                          │ 节点 B（NAT 后）     │
                                                          │  LocalApi 本地执行    │
                                                          └─────────────────────┘
```

关键点：**所有连接都是节点向大厅发起的出站连接**，节点无需公网 IP、无需端口映射。

数据流（A 下载 B 的文件）：

1. A 的 `PeerClient` 请求 `{C}/api/v1/relay/B/api/v1/resources/{id}/content?path=...`（带 Range 头）。
2. 大厅校验 B 在线，生成 `requestId`，通过 B 的控制通道下发请求元数据（含 A 的请求体，若有）。
3. B 用 `LocalApi` 在本地执行（读目录、读文件、算 ETag、处理 Range）。
4. B 新开一条出站 HTTP 流 `POST {C}/api/v1/relay/B/response/{requestId}`，先写一行 JSON 响应头，再流式写文件字节。
5. 大厅把状态码与响应头写给 A，并原样管道传输字节；A 的断点续传逻辑（`.rm-part`、`.rm-etag`）保持不变。

## 4. 大厅协议

### 4.1 端点

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/api/v1/lobby/health` | 大厅信息（名称、版本、在线数） |
| GET | `/api/v1/lobby/devices` | 在线设备列表 |
| POST | `/api/v1/lobby/register` | hello 注册（调试与测试用；在线状态由控制通道维持） |
| GET | `/api/v1/lobby/tunnel` | WebSocket 控制通道 |
| ANY | `/api/v1/relay/{deviceId}/api/v1/{**path}` | 请求转发入口 |
| POST | `/api/v1/relay/{deviceId}/response/{requestId}` | 响应回传通道 |

大厅只转发 `/api/v1/` 下的协议路径，不提供通用 HTTP 代理。

### 4.2 控制通道消息（JSON，camelCase）

- 客户端 → 大厅 `join`：
  ```json
  { "type": "join", "deviceId": "…", "nickname": "…", "avatar": "<base64 或 null>", "appVersion": "0.3.0" }
  ```
- 大厅 → 客户端 `joined`：`{ "type": "joined", "devices": [ … ] }`
- 大厅 → 客户端 `devices`：在线列表变化时广播完整列表（规模为小型内网大厅，列表本身很小）
- 大厅 → 客户端 `request`：
  ```json
  { "type": "request", "id": "…", "originDeviceId": "A", "method": "GET",
    "path": "/api/v1/resources", "headers": { "range": "bytes=0-" }, "body": "<base64 或 null>" }
  ```
- 双向心跳：`{ "type": "ping" }` / `{ "type": "pong" }`

在线状态与 WebSocket 生命周期绑定：连接即上线、断开即下线；头像内联在 `join` 中（已限制 512 KB），因此 `join` 帧上限约 700 KB。

### 4.3 转发请求

- 大厅剥离前缀 `/api/v1/relay/{deviceId}`，把剩余路径（`/api/v1/...`）转发给目标节点。
- 请求头只转发 `Range` 与 `If-Range`；方法原样保留；请求体（仅 `hello` 会用到）内联上限 **2 MB**，超限返回 413。
- 目标离线 → `503`；目标 15 秒内未开始响应 → `504`。
- 请求体随 `request` 消息一次性下发，不需要单独的上传端点。

### 4.4 响应回传

B 的响应体由一条**独立出站 HTTP 流**回传，不需要在 WebSocket 上自研多路复用：

- `POST /api/v1/relay/{deviceId}/response/{requestId}`，`deviceId` 必须是 `requestId` 的目标设备（防止第三方代答）。
- 请求体 = 一行紧凑 JSON 头 + 原始响应字节（`Content-Type: application/octet-stream`，分块传输）：
  ```text
  {"status":206,"headers":{"etag":"\"…\"","content-range":"bytes 128-1023/1024","content-length":"896","last-modified":"…","content-type":"application/octet-stream","content-disposition":"attachment; filename=\"…\""}}
  <原始字节…>
  ```
- 大厅解析首行，把状态码与允许的响应头写给等待中的 A 请求，其余字节流式转发，全程不落盘。
- `requestId` 一次性使用；未知或过期返回 404；A 已断开时返回 409 并通知 B 停止发送。

### 4.5 超时与上限（默认值，均可配置）

| 项 | 默认值 |
| --- | --- |
| 等待对方响应头 | 15 秒 |
| 响应流空闲 | 60 秒 |
| 请求体内联上限 | 2 MB |
| 单节点并发转发请求 | 4 |
| 大厅并发转发请求总数 | 32 |
| 在线设备上限 | 200 |
| 单 IP 请求速率 | 30 req/s（限流中间件） |
| 控制通道心跳 | 20 秒发送；60 秒无响应判离线 |

### 4.6 错误语义

| 状态码 | 含义 | 客户端表现 |
| --- | --- | --- |
| 503 | 目标不在线 | 设备显示「离线/无法连接」 |
| 504 | 目标未在时限内响应 | 同上，短暂延迟后恢复 |
| 413 | 请求体过大 | 报错 |
| 404 | 未知 `requestId` 或路径不合法 | 报错 |
| 409 | 请求已被取消 | 任务标记中断，可继续 |

## 5. 节点侧设计

### 5.1 LocalApi 抽取

从 `PeerNode` 中抽出与 HTTP 上下文无关的本地处理逻辑，直连与中继共用：

- `LocalApi.HandleAsync(LocalRequest) -> LocalResponse { StatusCode, Headers, Body }`
- `LocalRequest`：`Method`、`Path`、`Headers`（`Range`、`If-Range`）、`Body`、`Origin`
- `Origin`：
  - `Direct(ip)`：局域网直连来源
  - `Relay(lobbyUrl, originDeviceId)`：大厅中继来源
- 端点：`/health`、`/hello`、`/resources`、`/resources/{id}/tree`、`/resources/{id}/content`
- `PeerNode` 的 ASP.NET 端点退化为薄包装（传入 `Origin.Direct(RemoteIp(context))`），行为不变

两个必须处理的语义差异：

1. **hello 来源登记**：`Origin.Relay` 时把对方登记为 `PeerInfo(…, Ip = "", Port = 0, RelayUri = lobbyUrl)`，并跳过「该 IP 已属于另一台设备」校验；否则多台经大厅的设备都会被记成同一个回环地址而互相冲突。
2. **Range 处理**：`content` 自实现单区间 Range（`bytes=from-`、`bytes=from-to`）与 `If-Range` ETag 校验，语义对齐当前 `Results.File`：
   - 校验通过 → `206` + `Content-Range`，从偏移处读流
   - ETag 不匹配 → `200` 全量
   - 范围非法 → `416` + `Content-Range: bytes */length`
   - ETag 仍为 `"{长度:x}-{最后写入时间:x}"`，保证断点续传行为不变

路径安全继续沿用 `ResourceCatalog.ResolveFile`（穿越、链接、冒号校验），并用现有测试防回归。

### 5.2 LobbyClient（Core，新）

- 持有到大厅的 WebSocket 与状态机：加入、心跳、断线指数退避重连（1 秒 → 30 秒，仅在本机仍处于「已进入大厅」状态时重连）。
- 对外：`Uri? Lobby`、`bool IsConnected`、`IReadOnlyList<LobbyDevice> Devices`、`DevicesChanged` / `StateChanged` 事件。
- 收到 `request`：在并发上限内调度 → `LocalApi.HandleAsync` → 经响应回传通道流式写回。
- 请求取消（A 断开）时中止本地读取，不再发送。
- 断线期间大厅设备显示「大厅已断开」；重连成功后重新 `join` 并刷新列表。

### 5.3 PeerClient 中继路由

`PeerInfo` 增加 `RelayUri`，`Route(peer, route)` 分两种：

- `RelayUri is null` → 现有逻辑（IPv4 + 端口，保留 IPv4 校验）
- 否则 → `{RelayUri}/api/v1/relay/{deviceId}/api/v1/{route}`

`ConnectAsync`/`ProbeAsync`/`GetResourcesAsync`/`GetFilesAsync`/`OpenFileAsync` 的调用方不受影响。

### 5.4 地址簿与数据库

- `peers` 表新增 `relay_uri TEXT NULL`（用 `PRAGMA table_info` 判断后 `ALTER TABLE`，兼容旧库）。
- 新增 `lobbies(url TEXT PRIMARY KEY, last_used_utc TEXT NOT NULL)`：进入过的大厅留档，下次在界面上一键重连。
- 进入大厅时把在线设备自动登记为 peer（含昵称、头像、`relay_uri`）；离开大厅后记录保留，界面按离线显示。
- 设备仍以 `device_id` 为主键；同一设备若同时出现在两个大厅，以最近一次登记的为准（本期接受）。

### 5.5 超时策略修正

现状问题：`PeerClient` 的 `HttpClient.Timeout = 8s`（`ResourceManager.Core/PeerNode.cs:99`）是**整个请求（含读取响应体）**的总超时，大文件经中继或慢网会中途被掐断。

改为：

- `Timeout = Timeout.InfiniteTimeSpan`
- 控制类请求（hello、resources、tree）自建 8 秒 CTS
- 文件内容：连接与响应头 15 秒 CTS；读取阶段由 `DownloadManager` 维护空闲看门狗（60 秒无新字节则中止并置「已中断」，可继续）
- 该修正同时消除局域网慢速下载的既有隐患

### 5.6 现有模块影响

| 模块 | 改动 |
| --- | --- |
| `PeerNode.cs` | 端点薄化到 LocalApi；保留直连中间件 |
| `Models.cs` | `PeerInfo` 增加 `RelayUri`；新增大厅相关记录 |
| `NodeStore.cs` | `peers` 迁移、`lobbies` 表与读写 |
| `PeerClient` | 中继路由与超时拆分 |
| `DownloadManager` | 空闲读看门狗 |
| `MainWindow` | 大厅连接、列表与状态展示 |
| `ResourceCatalog` / `UpdateClient` | 不变 |

## 6. 界面变化

- 设置页新增「大厅」分组：
  - 地址输入框 + 「连接 / 断开」
  - 已保存大厅列表：一键连接、删除、显示上次使用时间
  - 状态行：`大厅：已连接（N 台在线）／未连接／重连中`
- 设备页：
  - 经大厅的设备带「经大厅」标记；在线 = 本机大厅连接 + 对方在列表
  - 手动 IPv4 连接方式照旧
- 收藏与下载沿用现有页面；大厅设备离线时沿用现有「离线/无法连接」「资源已撤销」文案
- 关闭到托盘后大厅连接保持；托盘「退出并停止共享」才断开

## 7. 安全与隐私

明确风险（本期按用户决策接受）：

- 大厅开放注册：知道地址的任何人都能进入、查看在线列表，并浏览所有在线节点已发布的资源；也能消耗 VPS 带宽中继文件。
- 大厅能看到全部中继明文；HTTPS 只保证客户端↔大厅，无端到端加密。
- 中继请求由大厅代为来源，不再经过发布端的 IP 白名单；白名单职责转移给大厅。

本期缓解措施：

- 只转发 `/api/v1/` 协议路径，只允许在线设备作为目标，不提供通用代理
- 请求体、并发、速率、设备数上限（见 4.5）
- 大厅状态仅在内存中，重启即清空
- 建议 TLS 反代（Caddy/nginx）与云防火墙最小放行
- 记录连接、限流与异常日志

后续可选（不在本期）：

- 邀请码 / 共享密钥、每设备 token、端到端加密、设备与资源审批

## 8. 部署

- 新项目 `ResourceManager.Relay`（ASP.NET Core 单文件自包含）：
  ```powershell
  dotnet publish .\ResourceManager.Relay\ResourceManager.Relay.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o .\dist\relay-linux-x64
  ```
  （win-x64 同理）
- 默认监听 `0.0.0.0:37642`，可配置；与应用默认端口相同但独立部署，互不冲突
- 建议 Caddy 反代并自动签发证书（WebSocket 自动透传）：
  ```text
  relay.example.com {
      reverse_proxy 127.0.0.1:37642
  }
  ```
- 客户端在设置页填 `https://relay.example.com` 后连接
- 可选提供 Dockerfile 与 systemd 单元示例

## 9. 测试计划

新增 `LobbyRelayTests`：在测试进程内启动大厅（真实 Kestrel，回环端口）+ 两个节点，全部走中继。

1. 加入、在线列表、离开广播
2. 经中继的 hello、资源目录、文件夹树
3. 经中继的文件与文件夹下载（含嵌套目录、空目录）
4. 经中继的断点续传（预置 `.rm-part` / `.rm-etag`，命中 206）
5. 对方离线 → 503 → 客户端显示离线；对方恢复后可继续
6. 路径穿越与链接路径仍被拒绝
7. 请求体超限 413、未知 `requestId` 404、非 `/api/v1/` 路径拒绝
8. 中继 hello 的来源登记：`relay_uri` 正确，多台中继设备不触发「该 IP 已属于另一台设备」
9. `If-Range` ETag 不匹配时回退 200 全量

现有直连测试必须全部保持通过（LocalApi 抽取的回归保护）。

## 10. 版本与发布

- 目标版本 **0.3.0**：按 `系统.模块.修改` 规则，新模块使模块号 +1
- `ResourceManager.App.csproj` 与 `ResourceManager.Relay.csproj` 同步为 0.3.0；Git 提交信息带 `0.3.0`
- `release.yml` 增加大厅构建产物：`ResourceManager.Relay-win-x64.exe`、`ResourceManager.Relay-linux-x64`
- README 增加大厅使用与部署章节；CHANGELOG 记录

## 11. 备选方案与取舍

| 方案 | 结论 | 原因 |
| --- | --- | --- |
| TCP/UDP 打洞 | 放弃 | 程序是 TCP HTTP、无 UDP 通道，对称 NAT/CGNAT 下成功率低 |
| frp/rathole 端口映射 | 放弃 | 需每节点独立公网 IP 且公网端口=监听端口；来源 IP 被改写会破坏地址簿与白名单 |
| 回环 HTTP 执行转发请求 | 放弃 | 需放开回环白名单，且发布端会把对方记成 `127.0.0.1`，多设备冲突；改用 LocalApi |
| WebSocket 多路复用响应体 | 放弃 | 需自研流控与分帧；改为「控制 WS + 出站 HTTP 响应流」，Range 原样透传 |
| 长轮询替代 WebSocket | 保留为降级选项 | 可行但延迟与连接开销更大；WS 更贴合在线大厅语义 |
| 自动直连回退 | 本期不做 | 用户明确「在大厅相遇即经大厅」；后续如需要可加同网段探测 |

## 12. 未决问题

1. 大厅是否展示公告或名称？本期仅在线列表
2. 4.5 中的上限默认值是否按文档实施
3. 未来引入邀请码时是否保留「开放进入」开关
4. 是否把大厅设备独立成一个「大厅」导航页（本期并入设备页）
