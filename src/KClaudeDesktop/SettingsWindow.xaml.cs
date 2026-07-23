using System.Windows;
using System.Windows.Controls;
using KClaudeDesktop.Core;

namespace KClaudeDesktop;

public partial class SettingsWindow : Window
{
    private readonly AppPaths _paths;
    private readonly ConfigurationStore _configuration;
    private readonly SecretStore _secrets;
    private readonly ClaudeCliLocator _locator;
    private readonly ClaudeConfigurationService _claudeConfiguration;
    private readonly DiagnosticsService _diagnostics;
    private readonly InstallService _installer;
    private readonly ClaudeUpdateService _updater;
    private readonly bool _openDiagnostics;
    private CancellationTokenSource? _operationCancellation;
    private ClaudeUpdateCheck? _latestCheck;

    public SettingsWindow(
        AppPaths paths,
        ConfigurationStore configuration,
        SecretStore secrets,
        ClaudeCliLocator locator,
        ClaudeConfigurationService claudeConfiguration,
        DiagnosticsService diagnostics,
        InstallService installer,
        ClaudeUpdateService updater,
        bool openDiagnostics)
    {
        InitializeComponent();
        _paths = paths;
        _configuration = configuration;
        _secrets = secrets;
        _locator = locator;
        _claudeConfiguration = claudeConfiguration;
        _diagnostics = diagnostics;
        _installer = installer;
        _updater = updater;
        _openDiagnostics = openDiagnostics;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var config = _configuration.LoadSwitcherConfig();
        ContextCombo.SelectedIndex = config.MemberContext == "256k" ? 1 : 0;
        AutoCheckUpdatesCheck.IsChecked = _configuration.LoadUiSettings().CheckUpdatesOnStartup;
        RefreshSecretStatus();
        await RefreshCliStatusAsync();
        RefreshBackups();
        if (_openDiagnostics)
        {
            SettingsTabs.SelectedIndex = 3;
            await RunDiagnosticsAsync();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _operationCancellation?.Cancel();
        var settings = _configuration.LoadUiSettings();
        settings.CheckUpdatesOnStartup = AutoCheckUpdatesCheck.IsChecked == true;
        _configuration.SaveUiSettings(settings);
    }

    private void SaveKeysButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MemberKeyBox.SecurePassword.Length > 0)
            {
                using var secure = MemberKeyBox.SecurePassword.Copy();
                secure.MakeReadOnly();
                _secrets.Save(ClaudeProfile.Member, secure);
            }
            if (ApiKeyBox.SecurePassword.Length > 0)
            {
                using var secure = ApiKeyBox.SecurePassword.Copy();
                secure.MakeReadOnly();
                _secrets.Save(ClaudeProfile.Api, secure);
            }

            var config = _configuration.LoadSwitcherConfig();
            config.MemberContext = (ContextCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1m";
            config.DefaultProfile = "member";
            _configuration.SaveSwitcherConfig(config);
            MemberKeyBox.Clear();
            ApiKeyBox.Clear();
            RefreshSecretStatus();
            MessageBox.Show(
                this,
                "Key 与会员上下文已安全保存；Claude 用户配置和用户环境变量均未改动。默认仍为会员通道。",
                "保存成功",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshCliStatusAsync()
    {
        var result = await _locator.VerifyAsync();
        CliStatusText.Text = result.Detail;
    }

    private void RefreshSecretStatus()
    {
        var member = _secrets.GetStatus(ClaudeProfile.Member);
        var api = _secrets.GetStatus(ClaudeProfile.Api);
        MemberStatusText.Text = "会员 Key：" + member.Message;
        ApiStatusText.Text = "开放平台 Key：" + api.Message;
    }

    private void InstallCliButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "有 WinGet 时将安装官方 Anthropic.ClaudeCode 包；没有时只打开 Anthropic 官方安装说明。不会自动执行远程脚本，也不会调用模型。继续吗？",
            "安装 Claude Code CLI",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                _installer.LaunchOfficialClaudeInstaller();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "无法启动安装", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void RepairConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "将先备份，再字段级合并 Claude 用户配置。settings.json 中的冲突项会清理，用户环境变量只提示、不删除。继续吗？",
            "修复配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var changes = _claudeConfiguration.InitializeAndRepair();
            MessageBox.Show(this, string.Join(Environment.NewLine, changes), "配置完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void InstallAppButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "安装到当前 Windows 用户并创建开始菜单入口？不会请求管理员权限。",
            "安装 KClaude Desktop",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var path = _installer.InstallCurrentUser(DesktopShortcutCheck.IsChecked == true);
            MessageBox.Show(this, "安装完成：" + path, "安装完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetUpdateBusy(true);
            var result = await _updater.CheckForUpdateAsync();
            _latestCheck = result;
            DisplayUpdateCheck(result);
            var settings = _configuration.LoadUiSettings();
            settings.LastUpdateCheck = result.CheckedAt;
            settings.LastKnownLatestVersion = result.LatestVersion;
            _configuration.SaveUiSettings(settings);
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = "检查失败：" + exception.Message;
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private async void UpgradeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestCheck is null)
        {
            try
            {
                SetUpdateBusy(true);
                _latestCheck = await _updater.CheckForUpdateAsync();
                DisplayUpdateCheck(_latestCheck);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "升级前无法取得官方版本与文档：" + exception.Message, "安全停止", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                SetUpdateBusy(false);
            }
        }
        if (!_latestCheck.UpdateAvailable)
        {
            MessageBox.Show(this, _latestCheck.Message, "无需升级", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"准备升级到 {_latestCheck.LatestVersion}。\n\n" +
            (string.IsNullOrWhiteSpace(_latestCheck.ReleaseNotesUrl)
                ? "该版本没有可用的官方 Release 文档。\n\n"
                : $"官方升级文档：{_latestCheck.ReleaseNotesUrl}\n\n") +
            "应用将先备份当前 claude.exe、版本和 SHA-256，且不会自动删除备份。确认升级吗？",
            "手动升级 Claude Code",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        try
        {
            SetUpdateBusy(true);
            var result = await _updater.UpdateAsync(
                text => Dispatcher.Invoke(() => AppendUpdateLog(text)),
                _operationCancellation.Token);
            UpdateStatusText.Text = result.Message;
            AppendUpdateLog(result.Message);
            RefreshBackups();
            await RefreshCliStatusAsync();
            MessageBox.Show(
                this,
                result.Message + "\n\n回滚备份：" + result.Backup.ExecutablePath,
                result.Success ? "升级完成" : "升级需要处理",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            AppendUpdateLog("升级已取消；升级进程已停止。");
            UpdateStatusText.Text = "升级已取消";
        }
        catch (Exception exception)
        {
            AppendUpdateLog("升级失败：" + exception.Message);
            MessageBox.Show(this, exception.Message, "升级失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetUpdateBusy(false);
        }
    }

    private async void RollbackButton_Click(object sender, RoutedEventArgs e)
    {
        if ((BackupCombo.SelectedItem as ComboBoxItem)?.Tag is not ClaudeCliBackup backup)
        {
            MessageBox.Show(this, "请选择一个回滚版本。", "未选择备份", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = MessageBox.Show(
            this,
            $"确认恢复 {backup.Version}？恢复前会再次备份当前版本，并验证备份 SHA-256。",
            "回滚 Claude Code",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        try
        {
            SetUpdateBusy(true);
            var message = await _updater.RollbackAsync(
                backup,
                text => Dispatcher.Invoke(() => AppendUpdateLog(text)),
                _operationCancellation.Token);
            AppendUpdateLog(message);
            UpdateStatusText.Text = message;
            RefreshBackups();
            await RefreshCliStatusAsync();
            MessageBox.Show(this, message, "回滚完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendUpdateLog("回滚已取消。");
            UpdateStatusText.Text = "回滚已取消";
        }
        catch (Exception exception)
        {
            AppendUpdateLog("回滚失败：" + exception.Message);
            MessageBox.Show(this, exception.Message, "回滚失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetUpdateBusy(false);
        }
    }

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e) => await RunDiagnosticsAsync();

    private async Task RunDiagnosticsAsync()
    {
        DiagnosticsBox.Text = "诊断中…";
        try
        {
            var items = await _diagnostics.RunAsync();
            DiagnosticsBox.Text = string.Join(
                Environment.NewLine,
                items.Select(item => $"{item.Status,-4}  {item.Name}: {item.Detail}"));
            var pass = items.Count(item => item.Status == "PASS");
            var warn = items.Count(item => item.Status == "WARN");
            var fail = items.Count(item => item.Status == "FAIL");
            DiagnosticsBox.AppendText($"{Environment.NewLine}{Environment.NewLine}Summary: PASS={pass} WARN={warn} FAIL={fail}");
        }
        catch (Exception exception)
        {
            DiagnosticsBox.Text = "FAIL  诊断：" + exception.Message;
        }
    }

    private void RefreshBackups()
    {
        BackupCombo.Items.Clear();
        foreach (var backup in _updater.ListBackups())
        {
            BackupCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{backup.Version} · {backup.CreatedAt:yyyy-MM-dd HH:mm:ss}",
                Tag = backup
            });
        }
        if (BackupCombo.Items.Count > 0)
        {
            BackupCombo.SelectedIndex = 0;
        }
    }

    private void AppendUpdateLog(string text)
    {
        if (UpdateLogBox.Text.Length > 0)
        {
            UpdateLogBox.AppendText(Environment.NewLine);
        }
        UpdateLogBox.AppendText(text);
        UpdateLogBox.ScrollToEnd();
    }

    private void DisplayUpdateCheck(ClaudeUpdateCheck result)
    {
        UpdateStatusText.Text = result.Message;
        AppendUpdateLog($"[{result.CheckedAt:yyyy-MM-dd HH:mm:ss}] {result.Message}");
        if (!string.IsNullOrWhiteSpace(result.ManifestCommit))
        {
            AppendUpdateLog("官方 manifest commit: " + result.ManifestCommit);
        }
        if (result.BuildDate is not null)
        {
            AppendUpdateLog("构建时间: " + result.BuildDate.Value.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        }
        if (!string.IsNullOrWhiteSpace(result.ReleaseNotesUrl))
        {
            AppendUpdateLog("官方升级文档: " + result.ReleaseNotesUrl);
        }
        if (!string.IsNullOrWhiteSpace(result.ReleaseNotes))
        {
            AppendUpdateLog(Environment.NewLine + result.ReleaseNotes.Trim());
        }
        else
        {
            AppendUpdateLog("官方未为该版本提供单独的 Release 文档。");
        }
    }

    private void SetUpdateBusy(bool busy)
    {
        CheckUpdateButton.IsEnabled = !busy;
        UpgradeButton.IsEnabled = !busy;
        BackupCombo.IsEnabled = !busy;
        RollbackButton.IsEnabled = !busy;
    }
}
