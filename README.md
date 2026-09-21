# 资源管理器

公司内网使用的 Windows 点对点文件共享工具。每台运行软件的电脑同时提供自己的已发布资源，并能通过 IP 地址访问其他电脑；不需要集中服务器。

## 获取与运行

仓库保存源码，不提交约 203 MB 的打包程序。可以从 [GitHub Actions 构建记录](https://github.com/FennecMomo/ResourceManager/actions/workflows/ci.yml)下载最新的 `ResourceManager-win-x64` 构建产物，或按下文命令在本机生成 `dist/win-x64/ResourceManager.exe`。自包含的 Windows x64 程序无需在目标电脑另装 .NET。首次运行如 Windows 防火墙询问，请允许公司内网访问。

若想把程序固定安装到当前用户目录并在桌面创建快捷方式，生成 EXE 后运行 `powershell -ExecutionPolicy Bypass -File .\scripts\install-desktop-shortcut.ps1`。该脚本将 EXE 复制到 `%LOCALAPPDATA%\Programs\ResourceManager`，快捷方式使用程序内嵌图标；更新程序时重新运行即可。

1. 在“设置”中填写昵称、选择头像；本机默认监听 `37642` 端口，可修改。
2. 在“我的发布”中选择文件或文件夹，并选择“引用原位置”或“复制到软件管理目录”。引用模式会跟随原文件变化；复制模式保存发布时的副本。
3. 在“设备”中输入对方的 IPv4 地址和端口，点击“连接”。双方会自动记录彼此的昵称和地址，然后可浏览、收藏或下载对方发布的资源。
4. 文件夹下载会保留目录结构。网络中断后，到“下载”页选择任务并点击“继续选中任务”。已下载的本地文件在发布者离线时仍可使用。
5. 关闭窗口默认缩到托盘，继续共享；从托盘菜单选择“退出并停止共享”才会离线。可以在“设置”中更改关闭行为。

收藏只保存入口，不自动保存文件。发布者离线时收藏显示“离线/无法连接”；发布者在线但撤销资源时显示“资源已撤销”。首次连接会自动登记 IP，任何能连接到监听端口的电脑都能浏览和下载已发布资源。请只在预期的公司网络中运行。

本机资料、设备、发布记录、收藏和下载任务保存在 `%LOCALAPPDATA%\ResourceManager`。复制发布的文件也保存在此目录的 `library` 子目录中。软件不会公开未发布的路径；撤销复制发布时会删除这份管理副本。

## 开发与验证

仅支持 Windows 桌面运行。使用 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，在项目根目录执行：

```powershell
dotnet build .\ResourceManager.slnx
dotnet test .\ResourceManager.Tests\ResourceManager.Tests.csproj
dotnet publish .\ResourceManager.App\ResourceManager.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o .\dist\win-x64
```

项目分为 WPF 桌面程序、节点核心层和集成测试。核心层使用 SQLite 保存本机状态，使用应用内 HTTP 接口交换设备资料、资源目录和文件内容。测试会在两个本地节点之间验证互认、资源模式、文件与文件夹下载、离线收藏、撤销资源、路径边界及断点续传。

## 版本与许可

当前版本为 `0.1.1`，图标的矢量原稿位于 `assets/icon.svg`，使用 `python -m pip install pillow cairosvg` 和 `python tools/render_icon.py` 可重新生成 Windows ICO。变更记录见 [CHANGELOG.md](CHANGELOG.md)。项目采用 [MIT 许可证](LICENSE)。本机 SSH 密钥、签名文件和打包产物均不提交到仓库。
