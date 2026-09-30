# 客户端更新历程

Windows 0.7.6 起，主窗口侧栏的“更新历程”以竖向时间轴展示各版本的功能新增、改进和修复。版本从新到旧排列，当前程序版本单独标记；可按完整版本、部分版本或功能关键词检索。内容内置于 EXE，不依赖网络、服务器或本地 Git 仓库。

## 内容维护

面向用户的记录保存在 `ResourceManager.App/Assets/ReleaseHistory.json`，由项目作为资源嵌入。每次发布客户端版本时，在此文件新增对应版本、真实日期、简短标题及变更列表，并同步 `CHANGELOG.md` 和项目版本号。分类使用“新增”“改进”“修复”，描述用户可见的行为变化，避免直接抄录提交信息。

仅记录实际交付的客户端版本；合并进其他版本的草案不单列版本。Android 与独立服务器仍各自维护更新记录。本次追溯的历史包含 40 个 Windows 版本；历史日期依据仓库中的交付记录整理。

## 本次验证

2026-09-30，`dotnet run --project tools/HelpChecks --no-restore -- --render-preview` 通过 82 项检查，覆盖更新历程导航、既有设置/服务器索引、版本与内容完整性、功能搜索、精确版本搜索、空结果恢复、当前版本标记，以及最小 1010×670 和正常 1260×820 布局；原有帮助章节、MCP 与图片完整性检查同时通过。

预览由隔离资料中的 WPF 控件直接渲染，不显示窗口，不截取桌面。预览输出在 `dist/help-checks/history-1010.png` 和 `dist/help-checks/history-1260.png`。

`dotnet run --project tools/ChatChecks --no-restore` 通过 79 项既有隔离检查，聊天、发布与 MCP 操作未回归。Release 自包含单文件打包成功；构建报告 NU1900（NuGet 漏洞源不可达），未完成在线漏洞审计。

成品为 `dist/client-0.7.6/ResourceManager.exe`，文件版本 `0.7.6.0`，SHA-256 为 `f47e9c14fb8a89fdb32bf7e3a7f5523c885dbf578f0ea30087e549bdb574c343`。本机 0.7.5 已通过控制通道正常退出，0.7.6 后台启动后核对版本、实际 EXE 路径和原资料目录一致，8 个发布、7 台历史设备及 1 台服务器保留；独立反馈服务仍可读。已注册的 `resource-manager` MCP 命令路径同步到此 EXE，并重新读取核对。
