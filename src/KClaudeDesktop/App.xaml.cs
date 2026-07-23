using System.IO;
using System.Text.Json;
using System.Windows;
using KClaudeDesktop.Core;

namespace KClaudeDesktop;

public partial class App : Application
{
    public AppPaths Paths { get; } = new();
    public ConfigurationStore Configuration { get; }
    public SecretStore Secrets { get; }
    public ClaudeCliLocator CliLocator { get; }
    public ClaudeConfigurationService ClaudeConfiguration { get; }
    public DiagnosticsService Diagnostics { get; }
    public InstallService Installer { get; }
    public TerminalLauncher Terminal { get; }

    public App()
    {
        Configuration = new ConfigurationStore(Paths);
        Secrets = new SecretStore(Paths);
        CliLocator = new ClaudeCliLocator(Paths);
        ClaudeConfiguration = new ClaudeConfigurationService(Paths);
        Diagnostics = new DiagnosticsService(Paths, Configuration, Secrets, CliLocator);
        Installer = new InstallService(Paths);
        Terminal = new TerminalLauncher(Paths, Configuration, Secrets, CliLocator);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "KClaude Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        if (ApplicationStartup.Resolve(e.Args) == ApplicationStartupMode.Uninstall)
        {
            var uninstallWindow = new UninstallWindow(Installer);
            MainWindow = uninstallWindow;
            uninstallWindow.Show();
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--headless-doctor")
        {
            try
            {
                var items = await Diagnostics.RunAsync();
                var payload = new
                {
                    generatedAt = DateTimeOffset.Now,
                    pass = items.Count(item => item.Status == "PASS"),
                    warn = items.Count(item => item.Status == "WARN"),
                    fail = items.Count(item => item.Status == "FAIL"),
                    items
                };
                await File.WriteAllTextAsync(e.Args[1], JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(items.Any(item => item.Status == "FAIL") ? 1 : 0);
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(e.Args[1], JsonSerializer.Serialize(new { error = exception.Message }));
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--terminal")
        {
            HandleTerminalArguments(e.Args.Skip(1).ToArray());
            return;
        }

        var mainWindow = CreateMainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
        if ((Environment.ProcessPath ?? "").Contains("Setup", StringComparison.OrdinalIgnoreCase))
        {
            _ = mainWindow.Dispatcher.BeginInvoke(() => mainWindow.ShowSettings());
        }
    }

    private MainWindow CreateMainWindow() => new(
        Paths,
        Configuration,
        Secrets,
        CliLocator,
        ClaudeConfiguration,
        Diagnostics,
        Installer,
        Terminal);

    private void HandleTerminalArguments(string[] arguments)
    {
        var profile = ClaudeProfile.Member;
        var forwarded = arguments.ToList();
        if (forwarded.Count > 0)
        {
            var first = forwarded[0].ToLowerInvariant();
            if (first is "member" or "auth")
            {
                profile = ClaudeProfile.Member;
                forwarded.RemoveAt(0);
            }
            else if (first is "api" or "platform" or "payg")
            {
                profile = ClaudeProfile.Api;
                forwarded.RemoveAt(0);
            }
            else if (first is "setup" or "doctor" or "help")
            {
                var main = CreateMainWindow();
                MainWindow = main;
                main.Show();
                _ = main.Dispatcher.BeginInvoke(() => main.ShowSettings(first == "doctor"));
                return;
            }
        }

        try
        {
            if (BillingSafety.RequiresApiConfirmation(profile, alreadyConfirmed: false))
            {
                var answer = MessageBox.Show(
                    "即将通过开放平台按量计费 Key 启动 Claude Code。此操作可能产生费用，是否继续？",
                    "确认按量计费",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    Shutdown(0);
                    return;
                }
            }
            Terminal.Launch(Environment.CurrentDirectory, profile, forwarded);
            Shutdown(0);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "无法启动 Claude Code", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
