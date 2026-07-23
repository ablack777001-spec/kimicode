namespace KClaudeDesktop.Core;

public enum ApplicationStartupMode
{
    Normal,
    Uninstall
}

public static class ApplicationStartup
{
    public static ApplicationStartupMode Resolve(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && arguments[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase)
            ? ApplicationStartupMode.Uninstall
            : ApplicationStartupMode.Normal;
}
