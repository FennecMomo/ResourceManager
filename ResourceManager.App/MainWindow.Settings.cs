using System.Windows;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class MainWindow
{
    private readonly SettingsEditorServices? settingsServices;
    private bool ReadAutoStart() => settingsServices?.ReadAutoStart() ?? AutoStartManager.IsEnabled();
    private void WriteAutoStart(bool enabled)
    {
        if (settingsServices is { } services) services.WriteAutoStart(enabled);
        else AutoStartManager.SetEnabled(enabled);
    }
    private void SettingsError(string message, Exception error)
    {
        if (settingsServices is { } services) services.ShowError(message, error);
        else ShowError(message, error);
    }
    private SettingsLeaveChoice ConfirmSettingsLeave(string message) => settingsServices is { } services
        ? services.ConfirmLeave(message) : SettingsLeaveDialog.Ask(this, message);

    private string? savedGatewayId;
    private string savedGatewayName = "", savedGatewayIp = "";
    private bool refreshingGatewaySelection;
    private bool HasUnsavedGateway => GatewayNameBox.Text != savedGatewayName || GatewayIpBox.Text != savedGatewayIp;
    private bool HasUnsavedSettings => HasUnsavedGeneralSettings || HasUnsavedGateway;

    private void LoadGatewayEditor(GatewayInfo? gateway)
    {
        savedGatewayId = gateway?.Id;
        savedGatewayName = gateway?.Name ?? "";
        savedGatewayIp = gateway?.WanIp ?? "";
        GatewayNameBox.Text = savedGatewayName;
        GatewayIpBox.Text = savedGatewayIp;
        GatewayProgressText.Text = gateway?.LastStatus ?? "未检查";
        UpdateSettingsSaveHint();
    }

    private bool SaveGatewayDraft()
    {
        try
        {
            // Existing drafts retain their identity; new entries follow the Add button's IP matching rule.
            var id = savedGatewayId ?? store.GetGateways().FirstOrDefault(g => g.WanIp == GatewayIpBox.Text.Trim())?.Id;
            var gateway = store.SaveGateway(GatewayNameBox.Text, GatewayIpBox.Text, id);
            LoadGatewayEditor(gateway);
            RefreshGatewaysView(gateway.Id);
            return true;
        }
        catch (Exception ex) { SettingsError("保存路由器入口失败", ex); return false; }
    }
    private AppSettings? savedSettings;
    private bool savedAutoStart;
    private bool savingSettings;
    private bool restoringSettingsPage;
    private bool resolvingSettingsNavigation;
    private int currentPageIndex;

    private void InitializeSettingsEditor()
    {
        CaptureSavedSettings();
        NicknameBox.TextChanged += (_, _) => UpdateSettingsSaveHint();
        ListenPortBox.TextChanged += (_, _) => UpdateSettingsSaveHint();
        CloseToTrayBox.Checked += (_, _) => UpdateSettingsSaveHint();
        CloseToTrayBox.Unchecked += (_, _) => UpdateSettingsSaveHint();
        AutoStartBox.Checked += (_, _) => UpdateSettingsSaveHint();
        AutoStartBox.Unchecked += (_, _) => UpdateSettingsSaveHint();
        savedGatewayName = GatewayNameBox.Text;
        savedGatewayIp = GatewayIpBox.Text;
        GatewayNameBox.TextChanged += (_, _) => UpdateSettingsSaveHint();
        GatewayIpBox.TextChanged += (_, _) => UpdateSettingsSaveHint();
    }

    private bool HasUnsavedGeneralSettings => savedSettings is { } saved &&
        (NicknameBox.Text != saved.Profile.Nickname || ListenPortBox.Text != saved.ListenPort.ToString() ||
         CloseToTrayBox.IsChecked == true != saved.CloseToTray || AutoStartBox.IsChecked == true != savedAutoStart ||
         !(pendingAvatar ?? []).SequenceEqual(saved.Profile.Avatar ?? []));

    private void CaptureSavedSettings()
    {
        savedSettings = store.GetSettings();
        savedAutoStart = ReadAutoStart();
        UpdateSettingsSaveHint();
    }

    private void UpdateSettingsSaveHint()
    {
        if (SettingsSaveHint is null || SaveSettingsButton is null) return;
        SaveSettingsButton.IsEnabled = !savingSettings;
        SettingsSaveHint.Text = savingSettings ? "正在保存并应用设置…" : HasUnsavedGateway
            ? "路由器入口尚未保存 · 请使用入口的保存按钮" : HasUnsavedGeneralSettings
                ? "有未保存的修改 · 离开前请保存" : "设置已保存";
    }

    private void DiscardSettingsEdits()
    {
        if (savedSettings is not { } saved) return;
        NicknameBox.Text = saved.Profile.Nickname;
        ListenPortBox.Text = saved.ListenPort.ToString();
        CloseToTrayBox.IsChecked = saved.CloseToTray;
        AutoStartBox.IsChecked = savedAutoStart;
        pendingAvatar = saved.Profile.Avatar?.ToArray();
        AvatarPreview.Source = AvatarImage(pendingAvatar);
        GatewayNameBox.Text = savedGatewayName;
        GatewayIpBox.Text = savedGatewayIp;
        UpdateSettingsSaveHint();
    }

    private void SetSettingsNavigationPage(int page)
    {
        restoringSettingsPage = true;
        try
        {
            Tabs.SelectedIndex = page;
            NavList.SelectedIndex = page;
        }
        finally { restoringSettingsPage = false; }
    }

    private async Task<bool> CanNavigateFromSettingsAsync(int targetPage)
    {
        if (resolvingSettingsNavigation || savingSettings)
        {
            SetSettingsNavigationPage(currentPageIndex);
            return false;
        }
        if (currentPageIndex != 7 || targetPage == 7 || !HasUnsavedSettings) return true;
        // Restore both selectors before displaying a modal or awaiting save: every navigation path
        // (sidebar, download, toast, keyboard) must keep the editor and its draft together.
        SetSettingsNavigationPage(7);
        resolvingSettingsNavigation = true;
        try
        {
            var choice = ConfirmSettingsLeave(HasUnsavedGateway
                ? "设置或路由器入口有未保存的修改。保存并离开会同时保存这两部分。"
                : "设置有未保存的修改，请选择如何离开当前页面。");
            if (choice == SettingsLeaveChoice.KeepEditing) return false;
            if (choice == SettingsLeaveChoice.Save)
            {
                if (HasUnsavedGeneralSettings && !await SaveSettingsAsync()) return false;
                if (HasUnsavedGateway && !SaveGatewayDraft()) return false;
            }
            if (choice == SettingsLeaveChoice.Discard) DiscardSettingsEdits();
            SetSettingsNavigationPage(targetPage);
            return true;
        }
        finally { resolvingSettingsNavigation = false; }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await SaveSettingsAsync();

    private async Task<bool> SaveSettingsAsync()
    {
        if (savingSettings || exiting) return false;
        var previousAutoStart = ReadAutoStart();
        var persisted = false;
        savingSettings = true;
        UpdateSettingsSaveHint();
        try
        {
            if (!int.TryParse(ListenPortBox.Text, out var port) || port is < 1 or > 65535)
                throw new ArgumentException("监听端口须为 1 至 65535 的整数。");
            if (NicknameBox.Text.Trim().Length is < 1 or > 80)
                throw new ArgumentException("昵称须为 1 至 80 个字符。");
            var previousPort = store.GetSettings().ListenPort;
            WriteAutoStart(AutoStartBox.IsChecked == true);
            store.SaveSettings(NicknameBox.Text, pendingAvatar, port, CloseToTrayBox.IsChecked == true, autoUpdate: true);
            persisted = true;
            // Snapshot before the first await so edits made while networking restarts remain dirty.
            NicknameBox.Text = NicknameBox.Text.Trim();
            ListenPortBox.Text = port.ToString();
            CaptureSavedSettings();
            UpdateIdentity();
            FeedbackEnvironmentText.Text = EnvironmentSummary();
            var discoveryReady = settingsServices is { } services
                ? await services.ApplyNetwork(previousPort, port) : await ApplySettingsNetworkAsync(previousPort, port);
            SetStatus(discoveryReady
                ? "设置已保存。设备资料会在下次状态检查时同步给其他电脑。"
                : "设置已保存，但局域网自动发现不可用；仍可使用 IP 地址手动连接。");
            return !HasUnsavedGeneralSettings;
        }
        catch (Exception ex)
        {
            if (!persisted)
            {
                try { WriteAutoStart(previousAutoStart); } catch { }
            }
            SettingsError(persisted ? "设置已保存，但连接服务未能应用；请检查端口后重试" : "保存设置失败", ex);
            return false;
        }
        finally { savingSettings = false; UpdateSettingsSaveHint(); }
    }

    private async Task<bool> ApplySettingsNetworkAsync(int previousPort, int port)
    {
        if (!node.IsRunning || previousPort != port)
        {
            await discovery.StopAsync();
            await node.StopAsync();
            await node.StartAsync(port);
        }
        var ready = await TryStartDiscoveryAsync();
        if (previousPort != port) await MaintainMappingsAsync();
        return ready;
    }
}

internal sealed record SettingsEditorServices(Func<bool> ReadAutoStart, Action<bool> WriteAutoStart,
    Func<int, int, Task<bool>> ApplyNetwork, Func<string, SettingsLeaveChoice> ConfirmLeave,
    Action<string, Exception> ShowError);

internal enum SettingsLeaveChoice { KeepEditing, Save, Discard }
