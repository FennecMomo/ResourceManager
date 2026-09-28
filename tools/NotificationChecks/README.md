# 未读消息提醒隔离检查

在仓库根目录执行 `dotnet run --project tools/NotificationChecks/NotificationChecks.csproj`。

使用随机隔离数据库、实际 WPF 控件及注入的前台状态、托盘和浮窗输出。不会显示窗口或托盘，不发送网络消息，不操作用户桌面。
验证重启恢复、闪烁两阶段、静音及到期、后台和前台已读边界、99+、重复投递、清空记录、最新未读选择，以及提醒跳转与未保存设置保护。
