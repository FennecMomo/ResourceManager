# 应用站隔离检查

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

`--live` 才会读取正式公开 API，打印目录 revision、已发布项目数量与标题，以及默认可见数和隐藏测试项目数，并将这些项目绑定到同一个无窗口 WPF 页面；仍默认隐藏测试项目，不打开链接，也不下载远程图片。`--render-preview` 使用 WPF `RenderTargetBitmap` 把 1010×670、1260×820 的页面预览写入 `dist/share-checks/share-fixture-*.png`；与 `--live` 合用时另输出 `share-live-*.png`。这些图片来自本测试进程中的控件渲染，不是桌面截图；其中预览图由确定性的内存位图替身生成。

显式指定 `--live-images` 会启用正式目录和真实预览图加载，并断言当前页面绑定的封面及截图能够解码；可以配合 `--render-preview` 生成包含正式图片的 `share-live-*.png`。只有此标志会下载远程图片，默认检查及单独的 `--live` 均使用内存位图替身。运行示例：

```powershell
dotnet run --project tools/ShareChecks/ShareChecks.csproj -c Release -- --live-images --render-preview
```

检查使用真实 WPF Dispatcher 和控件，通过 `Measure` / `Arrange` 计算最小及正常窗口尺寸下的布局；不调用 `Show()`，不截取桌面、不操作鼠标键盘、不触碰正在运行的客户端或独立反馈服务端。测试资料写入忽略提交的 `dist/share-checks/data-*` 隔离目录。

默认检查将项目目录、链接打开器和图片加载器注入测试替身，不访问分享站、不下载图片，也不启动浏览器。检查覆盖首次进入懒加载、重复请求保护、名称/简介/标签/分类搜索、在线/下载与标签组合筛选、列表选择与详情绑定、真实按钮的链接目标、空目录与初次网络/数据异常、失败刷新保留列表和重试恢复，以及索引 10 的侧栏/Tab/页面标题统一为“应用站”和既有索引。

目录包含正常项目、`Demo=true` 项目以及 `Demo=false` 但带有精确“测试项目”标签的项目，验证默认隐藏、显式开关、选择测试标签自动显示、取消开关重置冲突标签、普通标签与查询及类型的交集、刷新保留有效标签和选择、失效标签回退，以及全为测试项目时的空状态提示。较长的“测试项目说明”标签用于验证正常项目不会被错误隐藏。

图片检查验证列表和详情封面、截图地址与替代文字绑定，并注入成功位图和损坏图片两种结果，确认失败占位不会阻断下载按钮。长详情在 1010×670 与 1260×820 下验证垂直滚动、筛选工具栏边界和下载入口可达。

`PreviewLoaderChecks` 使用内存 PNG/SVG 与假的 HTTP 消息处理器验证图片尺寸限制、SVG 文本和渐变渲染、拒绝外链/脚本/实体/递归内容、同源地址规则、禁止重定向、缓存、超限响应与取消。控件生命周期检查还覆盖未显示前懒加载、卸载取消和旧请求迟到不能覆盖新图。

这是后台控件与布局检查，不等同于用户在实际可见客户端中的视觉验收。
