# 聊天页隔离检查

从仓库根目录执行 `dotnet run --project tools/ChatChecks/ChatChecks.csproj`。

测试运行真实 WPF 控件和聊天队列，使用独立的 `dist/chat-checks/data-<随机 ID>` 数据库。
窗口和托盘均不显示，不调用桌面输入、不操作正在运行的客户端；发送泵替换为空操作，不向真实设备发送消息。

覆盖输入框 1–5 行伸缩、自动折行、超限滚动、清空后收缩、会话栏展开/收起与持久化、草稿保留、小窗口布局，以及发送、空白、按键重复、无效接收人和输入法组合事件保护。布局图像直接从离屏控件渲染到 `dist/chat-checks`。

输入法检查使用模拟的 WPF 组合事件，不等同于实际第三方中文输入法的端到端验证。
输入框采用 WPF 的 [MinLines / MaxLines](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.textbox.maxlines?view=windowsdesktop-10.0)，覆盖原样式固定高度后按视觉行数伸缩。
