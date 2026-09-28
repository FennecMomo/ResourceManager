# 发布页面隔离检查

在仓库根目录运行 `dotnet run --project tools/PublicationChecks/PublicationChecks.csproj`。
使用独立数据库和样本文件，不显示窗口、托盘或实际选择对话框，不操作用户桌面。
验证分组层级、文件夹按需展开、内部文件信息与独立发布操作的区分、默认组保护、分组权限入口、继承权限来源、离屏权限编辑器、两个窗口尺寸的布局、批量发布与异常路径。原生选择窗口不在隔离检查中打开。
离屏渲染图保存在 `dist/publication-checks`。
