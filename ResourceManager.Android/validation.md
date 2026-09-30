# Android 0.1.1 / Windows 0.7.5 实施与验证记录

日期：2026-09-30。需求：反馈 Issue `7fee3e9fb3ae4ad3a39b740493bb80db` 及用户确认的 Android 修订计划。Android 为可覆盖安装的测试版，Windows 同步修复发现调度。

## 实现内容

- **签名兼容**：Hello 的二进制头像 Base64、普通字符串、嵌套 JSON、字段顺序及空值规则与 .NET 一致。设备 ID 与 Keystore 密钥独立于昵称/头像。Hello 响应上限 768 KiB、头像上限为解码后 512 KiB，继续限制响应。错误区分签名、设备 ID、随机数、固定密钥变化、鉴权与超时。
- **连接与发现**：同入口合并握手，验证缓存 30 秒；业务请求独立签名，鉴权失败/网络变化使缓存失效。LAN 与历史入口立即并行，各有 4 个额度；LAN 2.5 秒内广播两次，候选先显示“正在验证”。握手预算 3 秒、前台轮次预算 8 秒，取消关闭实际 HTTP 请求和响应读取。设备出现不等待目录读取，Windows 路由器和服务器独立推进。入口去重、失败冷却、网络代次和旧失败保护保留离线设备及新的成功状态。
- **聊天界面**：消息、设备、我的发布、传输四入口，默认消息，设置从顶部进入。会话列表、左右气泡、资源卡片、1–5 行底部输入框，以及文件/文件夹/已有发布附件入口。资源浏览按设备隔离，新增发布独立确认、折叠高级设置。外部分享先选会话再确认私发。存储读取移到后台，草稿按会话持久化。
- **通用后台共享**：持久化开启意愿与实际状态分开。connectedDevice 前台服务在有效 Wi-Fi 上管理 CPU 唤醒锁/组播锁，断网、停止、退出释放；重连、地址变化、服务重建重新监听。旧服务取消任务不能覆盖新实例的在线状态。中断消息回队列，下载保留片段并暂停；主动停止不自行重启。设置展示通用电池优化及标准系统设置入口，额外限制无法检测时标为未知，无厂商或机型分支。
- **诊断**：记录生命周期、屏幕/Doze、网络变化、锁释放、探测与发现耗时，支持导出。常驻通知不是联网正常的证据。

SQLite 结构仍为版本 1；原身份、信任、发布、消息和下载记录沿用。线协议与广播格式未改变。

## 实际验证

设备检查使用项目专用无窗口 AVD `RM_Test_36` / `emulator-5580`（API 36 x86_64），未操作用户真实手机。未使用 computer-use、桌面截图、鼠标或键盘注入；界面操作使用 Compose 语义动作，键盘由隔离 Activity 的系统 API 展开，预览为 Compose 组件离线渲染。

| 层级 | 已执行结果与覆盖 |
| --- | --- |
| JVM 协议 | 5 项通过，包含真实 Windows 节点，本次 LiveDesktopTest 未跳过。P1363/DER、固定密钥、重放、篡改、Range 和路径校验保留通过 |
| .NET 签名向量 | Base64 特殊字符、null/空/1/2/3/524288 字节头像、中文、Emoji、字符串转义、字段顺序/空值逐字节或摘要比对并验签，篡改被拒绝 |
| 资料与身份 | 实际 .NET 节点运行中更换头像、强制重新握手后，设备 ID、公钥与已固定信任不变；Android 修改昵称及重建 Activity 后身份不变 |
| 正常设备测试 | Runner 报 OK (10 tests)，实际正常执行 6 项、按参数跳过 4 项。覆盖 CIO/Keystore、SAF、权限、发现/取消、聊天 UI、共享断网及服务重建 |
| 私发文件/文件夹 | Android 实际复制发布并入私发队列，接收方通过签名 API 读取文件、目录和空目录，文件 SHA-256 一致；公开目录无私发，非接收方返回 404。Windows 隔离检查覆盖其私发流程 |
| Android → .NET | 显式测试实际 Keystore 签名握手、文字消息回执、目录、文件；持久下载任务通过片段/ETag 续传，导出字节一致 |
| .NET → Android | 实际 PeerClient / DownloadManager 连接真实共享服务，验签、目录、文件字节和文字消息回执通过 |
| 20 个慢历史入口 | Android 健康 LAN 候选身份确认 217 ms，整轮 8014 ms；Windows 健康设备 1000 ms 内确认，整轮 8098 ms。8 秒截止后包含取消收尾的调度开销，均在 9 秒检查界限内；重复广播不阻塞，历史离线记录保留 |
| HTTP 取消 | 250 ms 取消半截 Hello 响应，底层 TCP 在 2 秒内关闭，未持久化取消结果 |
| 网络与共享 | Activity 退到 CREATED 后服务提供通信；实际关闭模拟器 Wi-Fi 进入等待并释放锁，重连重新监听，.NET 下载恢复。生命周期模拟销毁/重建服务恢复共享，主动停止后再次启动请求不恢复 |
| Doze | 强制 deep IDLE，未豁免显示“后台受限”；临时标准电池优化豁免后 .NET 验签、下载和文字接收通过。之后恢复电池/Doze 设置；不等于真机熄屏 30 分钟通过 |
| 0.1.0 → 0.1.1 | 旧 APK 写入恢复场景，停止隔离测试进程并覆盖安装。身份 preferences、数据库、草稿和片段在安装前后 SHA-256 相同；新版启动公钥/信任/发布保留，Sending→Queued、Running→Paused，片段/ETag 保留 |
| UI | 浅色、深色 + 字体 1.3 倍、320×640 小屏，导航、草稿切换、附件及系统 IME 展开检查通过；发送/附件可见，组件预览已检查 |
| Windows | 79 项隔离聊天/MCP/设备检查通过，全程隐藏、无托盘及桌面输入；Release 自包含单文件打包通过 |
| 构建/Lint | APK / 测试 APK 构建通过，Lint 无 error，保留依赖升级/KTX 等 warning。.NET 有 NU1900 漏洞源不可达警告，不表示漏洞审计已完成 |

签名证书 SHA-256：`5ec957a130f658bcb21ac06bf4b48bdba6a3d5e779c02355207520defb6d24c4`，与 0.1.0 一致。包名 `dev.resourcemanager.android`，versionName `0.1.1`、versionCode `2`。覆盖安装保留资料；卸载会删除私有资料与身份密钥。

## 复现

仓库根目录执行；SDK/JDK 与测试身份放在忽略的 `.tools/android/`，不得提交或重新生成签名密钥。adb 必须明确指定隔离设备。

```powershell
dotnet run --project tools/AndroidProtocolVectors -- serve .tools/android/interop-new 47642
# 另一个终端；构建脚本设置项目 JDK/SDK 路径
$env:RM_INTEROP_PORT = '47642'
$env:RM_INTEROP_DIRECTORY = (Resolve-Path .tools/android/interop-new).Path
./tools/android-build.ps1 -DeviceTests -DeviceSerial emulator-5580
./tools/android-build.ps1 -Package
dotnet run --project tools/AndroidProtocolVectors -- verify ResourceManager.Android/protocol/build/android-signature.json

# Android → Windows
adb -s emulator-5580 shell am instrument -w -e class dev.resourcemanager.android.NodeInstrumentedTest#actualAndroidToDesktop -e desktop true dev.resourcemanager.android.test/androidx.test.runner.AndroidJUnitRunner

# .NET → 真实 Android 共享服务；限时 5 分钟，另一个终端运行接收器
adb -s emulator-5580 forward tcp:45842 tcp:37642
adb -s emulator-5580 shell am instrument -w -e class dev.resourcemanager.android.SharingRuntimeTest#backgroundHostForDozeAndReconnect -e background true dev.resourcemanager.android.test/androidx.test.runner.AndroidJUnitRunner
dotnet run --project tools/AndroidProtocolVectors -- android .tools/android/receiver-new 45842
adb -s emulator-5580 shell run-as dev.resourcemanager.android touch files/background-stop

# 恢复：seed、只停止该测试应用、verify；也可在两阶段之间覆盖安装新版
adb -s emulator-5580 shell am instrument -w -e class dev.resourcemanager.android.ProcessRecoveryTest#recoverPersistedIdentityQueuesPartsAndDraft -e recovery seed dev.resourcemanager.android.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5580 shell am force-stop dev.resourcemanager.android
adb -s emulator-5580 shell am instrument -w -e class dev.resourcemanager.android.ProcessRecoveryTest#recoverPersistedIdentityQueuesPartsAndDraft -e recovery verify dev.resourcemanager.android.test/androidx.test.runner.AndroidJUnitRunner

dotnet run --project tools/ChatChecks
```

Windows 节点资料目录写入 `stop` 文件正常停止节点。LiveDesktopTest 无端口会 skip；互通、后台、恢复测试必须显式开启，不把正常 Runner 总数当作全部已执行。UI 变体结束后恢复字体、夜间模式和分辨率。

## 未完成的真机验证与边界

- **真实手机熄屏 30 分钟双向聊天、下载及 Wi-Fi 发现仍待验证**。未取得 ARM64 真机、Android 10 实机或真实路由器广播耗时证据；217 ms 为隔离候选回调与身份探测耗时，不能当作用户网络实测。
- 服务生命周期模拟、隔离进程重启、forced Doze 各有证据；不承诺所有省电策略必然重建服务或持续联网。额外限制未知时标为未知；[标准 Android 电池优化设置](https://developer.android.com/training/monitoring-device-state/doze-standby)由用户主动调整。
- 未用真实微信、QQ、飞书账号收发，使用标准 SEND/SEND_MULTIPLE 与 FileProvider；特殊文件提供器及第三方应用仍需使用反馈。
- 下载沿用长度/ETag 续传，测试另外摘要/逐字节核对；未新增通用端到端 SHA-256 元数据接口。HTTP 内容仍未加密。
- 服务器资源、推送唤醒、手机 MCP、APK 自动更新和商店签名不在本轮范围，服务端生产程序不改动。持续共享会增加耗电。

## 主要依赖许可

Kotlin、AndroidX/Compose、Ktor、OkHttp、kotlinx 及 Gradle 主要为 Apache-2.0，许可元数据随依赖保留。SDK/JDK、模拟器与测试密钥不入 Git；正式商店分发前须使用维护者保管的发行密钥并生成完整许可清单。
