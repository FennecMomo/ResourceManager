# Android 互通协议与验证计划 D1

本文基于 Windows 客户端 0.7.4、提交 `434442b8fbdeb65bb8ff5368273fd1a5d6f6ac5a`。这里列出的是需要冻结和验证的契约；没有运行过 Android 样机。它不替代源码，字段最终以基线实现和导出的测试向量为准。

## 1. 端点与能力矩阵

### 1.1 首版局域网节点

| 协议/端点 | 基线行为 | Android 计划 |
| --- | --- | --- |
| UDP 37643 | `ResourceManager.LanDiscovery.v1`，query/response；设备 ID、昵称、HTTP 端口 | 同协议主动发现与响应；发现成功后还要签名握手 |
| GET `/api/v1/health` | PeerHello 与 capabilities | 返回真实已实现能力和当前 HTTP 端口 |
| POST `/api/v1/auth/hello` | 签名首次信任、密钥固定、签名响应 | 双向兼容；不以昵称、IP 或首次广播当作已验证身份 |
| POST `/api/v1/hello` | 旧版普通登记路径 | 可兼容发现用途；Android 首版要求签名能力，不自行降低已建立信任 |
| GET `/api/v1/resources` | 可见公开资源列表 | 保留旧版资源列表兼容 |
| GET `/api/v1/resource-catalog` | 可见分组和资源 | 分组可见性、继承和白名单匹配桌面 |
| GET `/api/v1/resources/{id}` | 元数据，可区分撤销和无权限的既有语义 | 每次校验可见权限及源可用性 |
| GET `/api/v1/resources/{id}/tree` | RemoteFile 数组，文件资源的相对路径为空字符串 | URI 树转协议路径；保留空目录 |
| GET `/api/v1/resources/{id}/content?path=...` | HTTP 内容流、ETag、Last-Modified、Range | 有界流、HEAD、Range/If-Range、取消；不整体读入内存 |
| POST `/api/v1/chat/messages` | Text/Resource/PrivateResource、消息 ID、接收回执与去重 | 持久收发队列；明确收到消息与附件下载的区别 |
| GET `/api/v1/chat/resources/{id}`、`/tree`、`/content` | 收件人 header、身份/入口检查和私发资源隔离 | 同语义实现，跨网入口变化必须验证 |
| POST `/api/v1/reminders` | 来源与资源核对、定向提醒 | 接收后通知；拒绝时提供准确失败原因 |

依据：[节点路由](../../../ResourceManager.Core/PeerNode.cs)、[发现](../../../ResourceManager.Core/LanDiscoveryService.cs)、[数据模型和能力常量](../../../ResourceManager.Core/Models.cs)、[聊天](../../../ResourceManager.Core/ChatService.cs)、[提醒](../../../ResourceManager.Core/ReminderService.cs)。

Android 只在对应实现完成后声明 `signed-device-v1`、`resource-groups-v1`、`resource-access-v1`、`chat-v1`、`chat-private-resource-v1`、`reminder-v1` 等能力。具体能力清单在 M1/M3 逐项验证，不照抄 Windows Self()。首版不声明 UPnP、router-discovery、Git、Windows 更新能力。

### 1.2 后续 workspace

| 范围 | 基线操作 | 兼容重点 |
| --- | --- | --- |
| 身份和成员 | capabilities、join、members 长轮询、leave | 服务器身份固定、签名登记、会话过期、401/403/409 区别 |
| 发布目录 | PUT catalog、GET catalogs | 资源所有者、权限、服务器发布范围、缓存在线状态 |
| 发布者在线中继 | relay、relay/poll、relay/{id} | 原始操作仅 describe/tree/meta/chunk；签名请求、取消和超时 |
| 服务器副本 | storage/uploads、entry、chunk、verify、commit、permissions、storage/read | 256 KiB 块、偏移确认、SHA-256、仅所有者管理及最终提交 |

依据：[WorkspaceProtocol](../../../ResourceManager.Core/WorkspaceProtocol.cs)、[WorkspaceResources](../../../ResourceManager.Core/WorkspaceResources.cs)、[WorkspaceStorage](../../../ResourceManager.Core/WorkspaceStorage.cs)、[服务端资源中继](../../../ResourceManager.Server/WorkspaceHub.Resources.cs)。

本基线没有服务器聊天邮箱、聊天中继、系统推送 token、消息离线保留与推送回执。资源中继存在不代表这些能力存在。Android 不在线时，其引用资源仍不可从中继读取；上传完成的服务器持久副本才可独立于手机在线状态使用。

## 2. 跨语言签名契约

### 2.1 点对点请求

[PeerProof](../../../ResourceManager.Core/PeerAuthentication.cs) 采用 P-256、SHA-256、SPKI 公钥和 Base64 签名；默认 .NET ECDSA SignData 输出 IEEE P1363 定长 r||s。Android Keystore 常规 ECDSA 签名需要按实际 provider 输出处理 DER/P1363 转换，转换函数必须覆盖整数正数填充、前导零与长度边界，不能简单截取字节。[.NET 签名格式](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dsasignatureformat?view=net-10.0)、[Android 签名 API](https://developer.android.com/reference/java/security/Signature)。

请求 headers：`X-RM-Device`、`X-RM-Time`、`X-RM-Nonce`、`X-RM-Key`、`X-RM-Signature`。待签内容是以下字符串数组的 System.Text.Json UTF-8 输出：

```text
[
  "signed-device-v1",
  senderDeviceId,
  targetDeviceId,
  HTTP method,
  PathAndQuery + "\n" + RangeHeader + "\n" + IfRangeHeader,
  unixSecondsAsInvariantText,
  nonce,
  uppercaseHex(SHA256(actualRequestBodyBytes))
]
```

没有 body 时仍计算空字节数组的 SHA-256。签名后不可由 HTTP 库重新编码 query 或重序列化 body；Range 与 If-Range 也在签名内。接收端不能为了宽容匹配而把无效签名当作普通未签名请求处理。

JSON 对象字段顺序、null、枚举、Base64、时间格式、Unicode 和 HTML 敏感字符的转义必须核对。尤其握手响应将序列化后的 PeerHello 作为外层数组中的一个字符串，存在嵌套转义；“语义等价 JSON”不保证签名字节相同。

当前点对点 nonce 为 24 随机字节的 48 字符十六进制，时间窗口为前后五分钟。接收端保存防重放记录并限制规模。向量要覆盖窗口内合法请求、过期、时钟偏差、重复 nonce 与目标设备不匹配。

首次信任遵守产品既定行为；后续同一 ID 的公钥变化必须拒绝，不能自动覆盖信任。握手阶段签名失败不能回退旧版无签名通道。

### 2.2 Workspace 签名差异

workspace 的注册字符串和 session 到期时间使用各自的序列化规则，不能直接复用点对点请求拼接。`WorkspaceResourceRules.SigningBytes` 序列化签名置空后的请求对象，字段顺序和 null 同样影响验签。资源请求 nonce 长度为 32、时间窗口为 120 秒，与点对点不同。

Android 时间解析/格式化不得丢失服务端 `DateTimeOffset.ToString("O")` 用到的精度和 offset 表示。优先使用固定测试向量确认重建方法；若必须调整协议，新增明确可协商版本，而非静默改变 workspace-v1 的签名字节。

### 2.3 计划产出

未来在 `tests/protocol-vectors/` 放入非生产密钥、固定请求/响应、待签原始字节的 Base64/hex、预生成签名及预期验签结果。该目录当前尚未创建。

C# 与 Kotlin 都运行相同向量。ECDSA 可以有随机性，测试验签成功和待签字节一致，不要求每次生成的签名与固定签名字节完全相等。补充动态测试：C# 签名由 Kotlin 验证，Kotlin/Keystore 签名由 C# 验证。

## 3. 文件传输语义

### 3.1 Android 作为发布者

- 文件资源 tree 返回一个 `RelativePath=""` 条目；文件夹使用 `/` 分隔相对路径。
- 准备可稳定读取的源之后才公开可下载状态。长度未知或不支持 seek 的流进入复制准备任务。
- 为同一文件版本提供稳定 ETag；版本变化返回新的标识，If-Range 不匹配不能错误追加旧片段。
- GET/HEAD 正确返回 Content-Length、ETag、Content-Range 和 200/206/416；接收端当前使用单 Range，异常和多 Range 行为也要有明确响应。
- 大文件长度、偏移和计数使用 64 位；空文件、空目录、零偏移及刚好 EOF 都有用例。
- 访问受管副本和引用文件均重新检查权限，撤销发布后新请求不能继续访问；活动流的关闭语义需明确并测试。
- 慢客户端、断连接和任务取消释放文件描述符及 URI 流，不无限缓存响应正文。

### 3.2 Android 作为下载者

- 每个文件保存 resourceId、来源身份、相对路径、长度、版本标识、已写偏移和输出映射。
- 206 必须与请求偏移一致；服务端因 If-Range 变化返回 200 时重置本任务片段，不能拼接到旧文件。
- 416、目录变化、资源撤销、401/403、磁盘不足、授权丢失分别记录；鉴权失败不降级为未签名请求。
- 断线保留可证明有效的片段；进程恢复检查实际文件长度，不只相信数据库计数。
- SAF 输出采用受管区暂存后导出。导出失败保留完成源和待导出记录；应用内任务只在输出最终成功后标记 Completed。
- 源或目标路径冲突/越界失败时报告具体条目；部分目录已完成不等于整项成功。

### 3.3 哈希能力的真实边界

普通点对点的内容端点提供 ETag/长度，基线没有独立 `/meta` SHA-256 接口；`DownloadManager` 的 metadata 可为空。文件接收后计算一个本地 SHA-256 只能得到本地摘要，没有可信来源摘要就不能称为与源完整性比对。

workspace 资源协议有 `meta` 的 SHA-256，服务器存储上传也有逐文件哈希验证。不得把这项保证自动写到所有直连资源上。

首版兼容基线时，点对点采用现有长度/ETag/HTTP 传输语义，测试中对已知夹具额外计算两端 SHA-256 证明结果一致。如产品要求运行时所有直连下载必须有源哈希，则另加可协商的文件元数据能力，修改 Windows 发布与下载两侧，明确旧客户端降级提示。这是评审决策项，不是已实现功能。

## 4. 本地数据和权限一致性

引用 URI、受管副本、私发副本、下载片段与导出文件使用不同来源类型。网络 resourceId 不暴露本机完整 URI；外部不能指定 Android 绝对文件路径。

持久授权是可撤回的，不是永远有效。读文件前捕获权限和来源失效，显示 NeedsPermission/SourceUnavailable；不会因为找不到文件就自动撤销用户发布记录。停止共享仅影响在线服务，不抹除设备、消息和发布。

设备身份、服务器身份、资源 ID 与 IP 分开存储。服务器中同一资源的在线中继版与存储副本不能混淆；服务器绑定删除后仍能看出收藏失效原因并移除本机收藏。

私发资源验证收件人 header、已验证设备、私发资源归属及合法连接入口。IP 变化可能触发现有入口约束，必须用新握手更新可信入口后再下载，不能为了可用性去掉收件人限制。

## 5. 验证矩阵和阶段门槛

以下全部为计划项，初始状态均为未执行。

| ID | 方式 | 场景与成功判据 | 门槛 |
| --- | --- | --- | --- |
| P01 | C# + Kotlin 单元测试 | 中英文、引号、emoji、null、枚举、UTC/offset、query 编码的待签字节完全相同 | M1 |
| P02 | 双向互通 | P-256 公钥与 P1363/DER 转换正确；双方接受合法签名并拒绝变造 | M1 |
| P03 | 隔离节点 | 密钥改变、重放、过期、目标错误、伪造能力均不降低认证 | M1 |
| N01 | 真机 + 隔离 Windows 节点 | UDP 双向发现，手动地址补充，节点端口冲突正确公告 | M1 |
| N02 | 隔离慢端点 | 快设备先出现，慢源超时独立；拒绝权限有准确状态 | M1/M3 |
| T01 | HTTP 互通 | 双向传输空文件、普通文件、超过 4 GiB 文件；64 位进度及夹具哈希一致 | M1/M2 |
| T02 | 故障注入 | 206/200/416、网络断开、进程中断、源改变后续传不拼接错误内容 | M2 |
| F01 | 真机 provider | 重启后 URI 授权仍可用；移动/删除/撤回权限时提示正确 | M2 |
| F02 | provider 测试夹具 | 非 seek、未知长度、低速/失败读取走复制流程或准确拒绝 | M2 |
| F03 | 目录夹具 | 空目录、Unicode、保留名、大小写冲突、深目录、恶意路径与 `.git` 隔离 | M2 |
| F04 | 空间/导出故障 | 下载中和导出中空间不足，取消/重启后可恢复且不覆盖用户文件 | M2 |
| A01 | 双向访问 | 公开/私有/继承/白名单与撤销，未经授权设备不可读；私发不出现在公开目录 | M2/M3 |
| C01 | 双向聊天 | 文字、多行、重试、重复投递和离线后重连，消息记录不重复 | M3 |
| C02 | 双向私发 | 文件/文件夹私发、收件人校验、准备失败、原文件变化、撤销与入口变化 | M3 |
| S01 | instrumentation + 目标 App | 单/多文件 URI 接收、授权过期、类型不符和分享结果；目标 App 实际兼容性单列 | M3 |
| B01 | 真机 | 前后台、锁屏、Doze、系统停止、Wi-Fi 切换；通知/任务/在线状态一致 | M3 |
| B02 | 平台版本矩阵 | 通知权限拒绝、局域网权限拒绝/撤回、UIDT/FGS 的版本分支 | M3 |
| W01 | 隔离服务器 | join、会话过期、身份变化、403/409，慢服务器不拖住本地刷新 | M4 |
| W02 | 跨网资源 | 目录、中继、存储副本、权限撤回、发布者离线及直连策略 | M4 |

Android 最低版本、通知权限版本、后台任务版本和目标 SDK 最新版本分别覆盖；至少一部实际目标手机和一个标准 Android 环境记录结果。仅在模拟器通过不等于厂商真机后台行为已验证。

测试默认使用临时数据库、非生产密钥、临时端口与自建文件夹具；不在用户真实发布上执行删除/重写，不启动真实对外消息。Windows 客户端测试采用现有无窗口机制。Android CLI/instrumentation 只用于指定测试设备，不以 computer-use 操作桌面或手机。

## 6. 样机报告必须留下的证据

- 测试日期、仓库提交、APK 哈希、签名类型、设备型号、系统/API、目标 SDK。
- 实际选用 HTTP 引擎、包体、关键依赖版本、许可证和失败案例。
- 握手/传输测试结果及脱敏日志，不能包含生产密钥、服务器 token 或私人文件正文。
- 网络断连、进程中断、权限撤回、锁屏实验的持续时间与观察结果；不要将短时间成功外推为全天在线。
- 对需要改动 Windows/服务器的接口，逐项列出兼容影响和 capability 协商策略。
- 未完成或无法执行的项目明确标为未验证。没有测试手机时，不越过对应真机门槛。
