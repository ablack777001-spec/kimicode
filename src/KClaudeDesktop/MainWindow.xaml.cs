using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KClaudeDesktop.Core;
using Microsoft.Win32;

namespace KClaudeDesktop;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly ConfigurationStore _configuration;
    private readonly SecretStore _secrets;
    private readonly ClaudeCliLocator _locator;
    private readonly ClaudeConfigurationService _claudeConfiguration;
    private readonly DiagnosticsService _diagnostics;
    private readonly InstallService _installer;
    private readonly TerminalLauncher _terminal;
    private readonly ClaudeUpdateService _updater;
    private readonly ClaudeCliRunner _runner;
    private CancellationTokenSource? _runCancellation;
    private string _sessionId = Guid.NewGuid().ToString();
    private bool _sessionStarted;
    private bool _apiConfirmed;
    private Paragraph? _assistantParagraph;

    public MainWindow(
        AppPaths paths,
        ConfigurationStore configuration,
        SecretStore secrets,
        ClaudeCliLocator locator,
        ClaudeConfigurationService claudeConfiguration,
        DiagnosticsService diagnostics,
        InstallService installer,
        TerminalLauncher terminal)
    {
        InitializeComponent();
        _paths = paths;
        _configuration = configuration;
        _secrets = secrets;
        _locator = locator;
        _claudeConfiguration = claudeConfiguration;
        _diagnostics = diagnostics;
        _installer = installer;
        _terminal = terminal;
        _updater = new ClaudeUpdateService(paths, locator);
        _runner = new ClaudeCliRunner(paths, configuration, secrets, locator);
        MessagesBox.Document = new FlowDocument { PagePadding = new Thickness(0) };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var ui = _configuration.LoadUiSettings();
        ProjectPathBox.Text = Directory.Exists(ui.LastProjectDirectory) ? ui.LastProjectDirectory : _paths.UserProfile;
        ProfileCombo.SelectedIndex = ui.LastProfile == "api" ? 1 : 0;
        PermissionCombo.SelectedIndex = FindComboIndex(PermissionCombo, ui.PermissionMode, fallback: 0);
        ContinueCheck.IsChecked = ui.ContinueMostRecent;
        AppendIntro();

        var cli = await _locator.VerifyAsync();
        StatusText.Text = cli.Success ? "Claude Code 已就绪" : cli.Detail;
        if (cli.Success && ui.CheckUpdatesOnStartup &&
            (ui.LastUpdateCheck is null || DateTimeOffset.Now - ui.LastUpdateCheck > TimeSpan.FromHours(24)))
        {
            await CheckForUpdatesAsync(showDialog: false);
        }
        else if (!string.IsNullOrWhiteSpace(ui.LastKnownLatestVersion))
        {
            UpdateButton.Content = $"最新 {ui.LastKnownLatestVersion}";
        }
        var member = _secrets.GetStatus(ClaudeProfile.Member);
        var api = _secrets.GetStatus(ClaudeProfile.Api);
        if (!cli.Success || !member.CanDecrypt || !api.CanDecrypt)
        {
            ShowSettings();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveUiState();
        _runner.TryStop();
    }

    private void AppendIntro()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
        paragraph.Inlines.Add(new Run("欢迎使用 KClaude Desktop\n")
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindBrush("TextBrush")
        });
        paragraph.Inlines.Add(new Run("选择可信任的项目目录，然后发送任务。默认会员通道，不会自动产生按量 API 费用。")
        {
            Foreground = FindBrush("MutedBrush")
        });
        MessagesBox.Document.Blocks.Add(paragraph);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Claude Code 工作目录",
            InitialDirectory = Directory.Exists(ProjectPathBox.Text) ? ProjectPathBox.Text : _paths.UserProfile
        };
        if (dialog.ShowDialog(this) == true)
        {
            ProjectPathBox.Text = dialog.FolderName;
            NewSession();
        }
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        _apiConfirmed = false;
        RefreshProfileDisplay();
    }

    private void RefreshProfileDisplay()
    {
        try
        {
            var config = _configuration.LoadSwitcherConfig();
            var definition = ProfileEnvironment.GetDefinition(GetSelectedProfile(), config, _paths);
            ProfileDetailText.Text = $"{definition.BaseUrl}\n模型 {definition.Model}";
            if (definition.Profile == ClaudeProfile.Api)
            {
                BillingBadge.Background = new SolidColorBrush(Color.FromRgb(74, 48, 31));
                BillingBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(255, 205, 153));
                BillingBadgeText.Text = "按量计费 · 手动选择";
            }
            else
            {
                BillingBadge.Background = new SolidColorBrush(Color.FromRgb(38, 54, 46));
                BillingBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(189, 232, 200));
                BillingBadgeText.Text = "会员额度 · 默认";
            }
        }
        catch (Exception exception)
        {
            ProfileDetailText.Text = exception.Message;
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_runCancellation is not null)
        {
            return;
        }
        var prompt = InputBox.Text;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }
        if (!Directory.Exists(ProjectPathBox.Text))
        {
            MessageBox.Show(this, "请选择存在的项目目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var profile = GetSelectedProfile();
        if (profile == ClaudeProfile.Api && !_apiConfirmed)
        {
            var answer = MessageBox.Show(
                this,
                "你选择了开放平台按量计费通道。本应用不会自动切换到该通道。是否仅为当前窗口确认使用？",
                "确认按量计费",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
            _apiConfirmed = true;
        }

        var secret = _secrets.GetStatus(profile);
        if (!secret.CanDecrypt)
        {
            MessageBox.Show(this, "所选通道的 Key 尚未安全录入或无法解密。", "需要设置 Key", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowSettings();
            return;
        }

        AppendUserMessage(prompt);
        InputBox.Clear();
        BeginAssistantMessage();
        SetBusy(true);
        _runCancellation = new CancellationTokenSource();

        var request = new ClaudeRunRequest(
            ProjectPathBox.Text,
            profile,
            GetSelectedPermissionMode(),
            prompt,
            _sessionId,
            _sessionStarted,
            ContinueCheck.IsChecked == true);

        try
        {
            var result = await _runner.RunAsync(
                request,
                output => Dispatcher.Invoke(() => AppendClaudeOutput(output)),
                _runCancellation.Token);
            _sessionStarted = result.SessionStarted || _sessionStarted;
            StatusText.Text = result.ExitCode == 0 ? "完成" : $"Claude Code 退出码 {result.ExitCode}";
            if (result.ExitCode != 0)
            {
                AppendClaudeOutput(new ClaudeOutput(ClaudeOutputKind.Error, $"\n[退出码 {result.ExitCode}]\n"));
            }
        }
        catch (OperationCanceledException)
        {
            AppendClaudeOutput(new ClaudeOutput(ClaudeOutputKind.Status, "\n[已停止]\n"));
            StatusText.Text = "已停止";
        }
        catch (Exception exception)
        {
            AppendClaudeOutput(new ClaudeOutput(ClaudeOutputKind.Error, "\n" + exception.Message + "\n"));
            StatusText.Text = "运行失败";
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            SetBusy(false);
            SaveUiState();
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        _runner.TryStop();
    }

    private void NewChatButton_Click(object sender, RoutedEventArgs e) => NewSession();

    private void NewSession()
    {
        _sessionId = Guid.NewGuid().ToString();
        _sessionStarted = false;
        ContinueCheck.IsChecked = false;
        _apiConfirmed = false;
        MessagesBox.Document.Blocks.Clear();
        AppendIntro();
        StatusText.Text = "已新建界面会话";
    }

    private void OpenTerminalButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var arguments = ContinueCheck.IsChecked == true ? new[] { "-c" } : Array.Empty<string>();
            _terminal.Launch(ProjectPathBox.Text, GetSelectedProfile(), arguments);
            StatusText.Text = "已打开完整交互终端";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开终端", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private async void UpdateButton_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(showDialog: true);

    private async Task CheckForUpdatesAsync(bool showDialog)
    {
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "检查中…";
        try
        {
            var result = await _updater.CheckForUpdateAsync();
            var ui = _configuration.LoadUiSettings();
            ui.LastUpdateCheck = result.CheckedAt;
            ui.LastKnownLatestVersion = result.LatestVersion;
            _configuration.SaveUiSettings(ui);
            UpdateButton.Content = result.UpdateAvailable ? $"可升级 {result.LatestVersion}" : "已是最新版";
            if (result.UpdateAvailable)
            {
                UpdateButton.Background = new SolidColorBrush(Color.FromRgb(102, 73, 42));
                StatusText.Text = result.Message + "；请在设置中手动升级";
            }
            if (showDialog)
            {
                MessageBox.Show(
                    this,
                    result.Message +
                    (string.IsNullOrWhiteSpace(result.ReleaseNotesUrl) ? "\n官方未提供单独的 Release 文档。" : "\n升级文档已同步到设置页。") +
                    (result.UpdateAvailable ? "\n\n升级不会自动执行，请在设置页确认。" : ""),
                    "Claude Code 更新",
                    MessageBoxButton.OK,
                    result.UpdateAvailable ? MessageBoxImage.Information : MessageBoxImage.None);
            }
        }
        catch (Exception exception)
        {
            UpdateButton.Content = "检查更新";
            StatusText.Text = "版本检查失败：" + exception.Message;
            if (showDialog)
            {
                MessageBox.Show(this, exception.Message, "检查更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    public void ShowSettings(bool openDiagnostics = false)
    {
        var window = new SettingsWindow(
            _paths,
            _configuration,
            _secrets,
            _locator,
            _claudeConfiguration,
            _diagnostics,
            _installer,
            _updater,
            openDiagnostics)
        {
            Owner = this
        };
        window.ShowDialog();
        RefreshProfileDisplay();
    }

    private void AppendUserMessage(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 6, 0, 16) };
        paragraph.Inlines.Add(new Run("你\n") { FontWeight = FontWeights.SemiBold, Foreground = FindBrush("AccentBrush") });
        paragraph.Inlines.Add(new Run(text) { Foreground = FindBrush("TextBrush") });
        MessagesBox.Document.Blocks.Add(paragraph);
        MessagesBox.ScrollToEnd();
    }

    private void BeginAssistantMessage()
    {
        _assistantParagraph = new Paragraph { Margin = new Thickness(0, 0, 0, 18) };
        _assistantParagraph.Inlines.Add(new Run("Claude Code\n")
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(164, 202, 255))
        });
        MessagesBox.Document.Blocks.Add(_assistantParagraph);
        MessagesBox.ScrollToEnd();
    }

    private void AppendClaudeOutput(ClaudeOutput output)
    {
        if (_assistantParagraph is null)
        {
            BeginAssistantMessage();
        }
        var brush = output.Kind switch
        {
            ClaudeOutputKind.Error => new SolidColorBrush(Color.FromRgb(255, 143, 143)),
            ClaudeOutputKind.Tool => new SolidColorBrush(Color.FromRgb(219, 179, 112)),
            ClaudeOutputKind.Status => FindBrush("MutedBrush"),
            _ => FindBrush("TextBrush")
        };
        _assistantParagraph!.Inlines.Add(new Run(output.Text) { Foreground = brush });
        MessagesBox.ScrollToEnd();
    }

    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        ProfileCombo.IsEnabled = !busy;
        ProjectPathBox.IsEnabled = !busy;
        StatusText.Text = busy ? "Claude Code 正在工作…" : StatusText.Text;
    }

    private ClaudeProfile GetSelectedProfile() =>
        (ProfileCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "api"
            ? ClaudeProfile.Api
            : ClaudeProfile.Member;

    private string GetSelectedPermissionMode() =>
        (PermissionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "plan";

    private void SaveUiState()
    {
        var settings = _configuration.LoadUiSettings();
        settings.LastProjectDirectory = ProjectPathBox.Text;
        settings.LastProfile = GetSelectedProfile() == ClaudeProfile.Api ? "api" : "member";
        settings.PermissionMode = GetSelectedPermissionMode();
        settings.ContinueMostRecent = ContinueCheck.IsChecked == true;
        _configuration.SaveUiSettings(settings);
    }

    private static int FindComboIndex(ComboBox combo, string tag, int fallback)
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if ((combo.Items[index] as ComboBoxItem)?.Tag?.ToString() == tag)
            {
                return index;
            }
        }
        return fallback;
    }

    private Brush FindBrush(string name) => (Brush)FindResource(name);
}
