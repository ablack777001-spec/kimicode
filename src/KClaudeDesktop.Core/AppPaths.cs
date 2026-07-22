namespace KClaudeDesktop.Core;

public sealed class AppPaths
{
    public AppPaths(string? userProfile = null, string? switcherRoot = null, string? appDataRoot = null)
    {
        UserProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        SwitcherRoot = switcherRoot ?? Path.Combine(UserProfile, ".claude-kimi-switch");
        AppDataRoot = appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KClaudeDesktop");
    }

    public string UserProfile { get; }
    public string SwitcherRoot { get; }
    public string AppDataRoot { get; }
    public string SecretsRoot => Path.Combine(SwitcherRoot, "secrets");
    public string BackupsRoot => Path.Combine(SwitcherRoot, "backups");
    public string LogsRoot => Path.Combine(SwitcherRoot, "logs");
    public string ConfigPath => Path.Combine(SwitcherRoot, "config.json");
    public string MemberSecretPath => Path.Combine(SecretsRoot, "kimi-member-key.dpapi");
    public string PlatformSecretPath => Path.Combine(SecretsRoot, "kimi-platform-key.dpapi");
    public string UiSettingsPath => Path.Combine(AppDataRoot, "ui.json");
    public string UpdateInfoPath => Path.Combine(AppDataRoot, "latest-update.json");
    public string CliBackupsRoot => Path.Combine(AppDataRoot, "cli-backups");
    public string ClaudeJsonPath => Path.Combine(UserProfile, ".claude.json");
    public string ClaudeSettingsPath => Path.Combine(UserProfile, ".claude", "settings.json");
    public string UserBinRoot => Path.Combine(UserProfile, "bin");
    public string TerminalCommandPath => Path.Combine(UserBinRoot, "kclaude.cmd");
    public string InstalledAppRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "KClaude Desktop");
    public string InstalledExePath => Path.Combine(InstalledAppRoot, "KClaudeDesktop.exe");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(SwitcherRoot);
        Directory.CreateDirectory(SecretsRoot);
        Directory.CreateDirectory(BackupsRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(CliBackupsRoot);
    }
}
