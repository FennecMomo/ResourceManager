# 作品分享页隔离检查

在仓库根目录运行（需要 .NET 10 SDK）：

```powershell
dotnet run --project tools/ShareChecks/ShareChecks.csproj -c Release
```

使用仓库内安装的 SDK 时：

```powershell
& .\.tools\dotnet\dotnet.exe run --project tools/ShareChecks/ShareChecks.csproj -c Release
```

如需同时核对正式分享站 API 与生成控件离屏预览：

```powershell
& .\.tools\dotnet\dotnet.exe run --project tools/ShareChecks/ShareChecks.csproj -c Release -- --live --render-preview
```

`--live` 才会读取正式公开 API，打印目录 revision、已发布项目数量与标题，并将这些项目绑定到同一个无窗口 WPF 页面；不打开链接。`--render-preview` 使用 WPF `RenderTargetBitmap` 把 1010×670、1260×820 的页面预览写入 `dist/share-checks/share-fixture-*.png`；与 `--live` 合用时另输出 `share-live-*.png`。这些图片来自本测试进程中的控件渲染，不是桌面截图。

检查使用真实 WPF Dispatcher 和控件，通过 `Measure` / `Arrange` 计算最小及正常窗口尺寸下的布局；不调用 `Show()`，不截取桌面、不操作鼠标键盘、不触碰正在运行的客户端或独立反馈服务端。测试资料写入忽略提交的 `dist/share-checks/data-*` 隔离目录。

默认检查将项目目录与链接打开器注入测试替身，不访问分享站，也不启动浏览器。检查覆盖首次进入懒加载、重复请求保护、名称/简介/标签/分类搜索、在线/下载筛选、列表选择与详情绑定、真实按钮的链接目标、空目录与初次网络/数据异常、失败刷新保留列表和重试恢复，以及追加在索引 10 的侧栏/Tab 对应关系和既有索引。长详情在 1010×670 与 1260×820 下验证垂直滚动和下载入口可达。

这是后台控件与布局检查，不等同于用户在实际可见客户端中的视觉验收。
