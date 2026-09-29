# Android 0.1.0 实施与验证记录

日期：2026-09-29。需求：反馈 Issue `7fee3e9fb3ae4ad3a39b740493bb80db`。用户在独立审查工具无法连接后授权直接开发。

## 已落实的架构选择

- Kotlin + Compose；独立 Android Keystore P-256 身份。协议模块独立于 Android，能够在 JVM 验证。
- SQLiteOpenHelper 事务存储设备、分组、发布、消息和下载。首版用小型记录仓库，未引入 Room；状态通过 StateFlow 通知 UI。
- Ktor CIO 入站 HTTP、OkHttp 出站。CIO 已在 API 36 x86_64 模拟器实际提供服务，不再仅是文档候选。
- 自制浏览器浏览用户明确授权的 SAF 目录，支持按文件名和媒体类型筛选；不申请全盘管理或全量媒体库权限。第三方分享只在确认后复制。
- connectedDevice 前台共享服务由可见应用主动启动；实际启动、正常停止已测。下载使用相同共享期的可取消任务，进程退出后保留并暂停，不伪装成永久后台任务。
- 所有下载先落在应用私有目录；内容和分块缓存分目录存储，避免远端文件名与 `.part` / `.etag` 管理文件冲突。

## 实际验证

| 层级 | 结果与覆盖 |
| --- | --- |
| JVM 协议测试 | P1363/DER 双向转换、签名篡改、固定密钥、防重放、Range 签名、路径穿越拒绝通过 |
| Windows 原实现测试向量 | 反射调用实际 `PeerProof` 导出字节与签名；Kotlin 对中文、表情、全部 ASCII 转义、嵌套 Hello 和 Range 请求逐字节比对并验签通过 |
| Kotlin → .NET 验签 | Kotlin 生成签名，.NET ECDsa 按实际 P1363 格式验证通过 |
| JVM → 隔离 Windows 节点 | 真实握手、资源树、空目录、内容、206 续传、If-Range 改变后的 200 回退、私有分组隐藏通过 |
| API 36 Android → Windows | Android Keystore 身份实际连接 Windows 节点，读取目录与文件；持久下载任务使用已有片段/ETag 恢复，导出后字节一致 |
| Windows → API 36 Android | 使用仓库真实 `PeerClient` / `DownloadManager` 验证 Android Hello 签名、目录和文件内容通过 |
| API 36 设备测试 | CIO 服务、Keystore 稳定身份、文件内容与 Range、私有权限、聊天重复回执和内容冲突通过 |
| SAF 设备测试 | 独立测试 DocumentsProvider 授予持久 URI 权限；子目录引用、重新打开仓库、保留空目录、复制文件内容通过 |
| 生命周期设备测试 | 启动真实 Activity、开启 connectedDevice 服务、正常停止共享、身份保持通过 |
| 构建与静态检查 | APK / instrumentation APK 构建通过；Android Lint 无 error，保留依赖升级提示和 KTX 风格等 warning |

测试使用项目创建的无窗口 AVD `RM_Test_36` / `emulator-5580`。没有连接或控制用户真实手机，没有使用 computer-use、桌面截图驱动、鼠标键盘注入。Windows 测试节点监听独立 loopback 端口，使用 `.tools/android/interop-*` 隔离资料。

## 复现互通测试

先使用构建脚本准备 APK 和测试模块。以下命令从仓库根目录运行，测试节点运行期间保持终端会话；向其资料目录写入 `stop` 文件会正常退出。

```powershell
dotnet run --project tools/AndroidProtocolVectors -- serve .tools/android/interop-new 47642
# 另一个终端，配置 JAVA_HOME、ANDROID_HOME 等后：
$env:RM_INTEROP_PORT = '47642'
./ResourceManager.Android/gradlew.bat -p ResourceManager.Android :protocol:test --rerun-tasks
dotnet run --project tools/AndroidProtocolVectors -- verify ResourceManager.Android/protocol/build/android-signature.json

# Android → Windows（仅官方模拟器的 10.0.2.2 指向宿主）
adb -s emulator-5580 shell am instrument -w -e class dev.resourcemanager.android.NodeInstrumentedTest#actualAndroidToDesktop -e desktop true dev.resourcemanager.android.test/androidx.test.runner.AndroidJUnitRunner

# Windows → Android：启动 60 秒限时测试节点；另一个终端运行 .NET 客户端
adb -s emulator-5580 forward tcp:45842 tcp:37642
adb -s emulator-5580 shell am instrument -w -e class dev.resourcemanager.android.NodeInstrumentedTest#serveForDesktop -e serve true dev.resourcemanager.android.test/androidx.test.runner.AndroidJUnitRunner
dotnet run --project tools/AndroidProtocolVectors -- android .tools/android/receiver-new 45842
```

`LiveDesktopTest` 未配置 `RM_INTEROP_PORT` 时明确 skip；Android 的两项跨进程互通测试也必须通过参数开启。不能把常规测试中跳过的项目算作已执行。本记录的互通项目均额外显式开启执行过。

## 尚未取得的证据与后续边界

- 未测试真实 ARM64 手机、Android 10 实机、各厂商省电/锁屏/后台清理策略和真实 Wi-Fi 广播环境。模拟器证明协议与系统接口运行，不代表所有手机后台行为一致。
- 未使用真实微信、QQ、飞书账号进行收发；实现为标准 Android SEND/SEND_MULTIPLE 与 FileProvider。系统文件提供器、第三方云盘的特殊行为仍需要使用反馈。
- 没有把系统通知权限拒绝等同于消息丢失；消息先入库再通知。但真实厂商通知到达与点击体验尚未实机确认。
- 本次无服务器阶段、iOS、手机 MCP、APK 自动更新或发布商店签名。Windows 与服务端生产代码及运行实例不变。
- 本次为可安装测试版，以上边界随包公开；没有把未做的真机验证写成已经完成。

## 主要依赖许可

Kotlin、AndroidX/Compose、Ktor、OkHttp、kotlinx 系列主要采用 Apache-2.0；Gradle 采用 Apache-2.0。许可证与 NOTICE 随依赖保留在构建元数据中。JDK、SDK、模拟器和测试密钥不进入 Git。正式商店分发前须使用维护者保管的独立发行密钥并生成完整依赖许可清单。
