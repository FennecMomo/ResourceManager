# 帮助页截图与隔离检查

在仓库根目录运行：

```powershell
dotnet run --project tools/HelpChecks/HelpChecks.csproj -c Release -- --generate-assets
dotnet run --project tools/HelpChecks/HelpChecks.csproj -c Release -- --render-preview
```

第一条用临时资料目录和演示数据在后台创建 `MainWindow`，通过 WPF `RenderTargetBitmap` 渲染真实页面，再按控件布局给每一步截图画红框。它不显示窗口、不截取桌面、不触碰正在运行的客户端或反馈服务端。输出的 30 张图片写入 `ResourceManager.App/Assets/Help`，作为资源嵌入 EXE；改变对应页面布局或帮助步骤后应重新生成并检查图片。

第二条运行帮助页检查，并把帮助页的离屏预览写入忽略提交的 `dist/help-checks/help-preview.png`。不加参数运行时只做隔离检查；检查涵盖标题栏位置、十章内容、MCP 工具列表、所有截图资源的嵌入与尺寸及章节切换。
