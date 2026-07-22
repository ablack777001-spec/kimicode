using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace KClaudeDesktop.Core;

public sealed class InstallService
{
    private readonly AppPaths _paths;

    public InstallService(AppPaths paths)
    {
        _paths = paths;
    }

    public bool IsRunningSingleFile => string.IsNullOrWhiteSpace(Assembly.GetEntryAssembly()?.Location);

    public string InstallCurrentUser(bool createDesktopShortcut)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前 EXE。");
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("当前 EXE 不存在。", source);
        }
        if (!IsRunningSingleFile)
        {
            throw new InvalidOperationException("请从发布目录中的单文件 EXE 执行安装，而不是从开发构建运行。");
        }

        Directory.CreateDirectory(_paths.InstalledAppRoot);
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(_paths.InstalledExePath), StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(_paths.InstalledExePath))
            {
                var backup = Path.Combine(
                    _paths.InstalledAppRoot,
                    $"KClaudeDesktop.exe.{DateTime.Now:yyyyMMdd-HHmmssfff}.bak");
                File.Copy(_paths.InstalledExePath, backup, overwrite: false);
            }
            var temp = Path.Combine(_paths.InstalledAppRoot, $"KClaudeDesktop.{Guid.NewGuid():N}.tmp");
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, _paths.InstalledExePath, overwrite: true);
        }

        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "KClaude Desktop.lnk");
        CreateShortcut(startMenu, _paths.InstalledExePath, _paths.InstalledAppRoot);
        if (createDesktopShortcut)
        {
            var desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "KClaude Desktop.lnk");
            CreateShortcut(desktop, _paths.InstalledExePath, _paths.InstalledAppRoot);
        }

        Directory.CreateDirectory(_paths.UserBinRoot);
        var command = $"@echo off\r\nstart \"\" /wait \"{_paths.InstalledExePath}\" --terminal %*\r\nexit /b %ERRORLEVEL%\r\n";
        AtomicFile.WriteUtf8(_paths.TerminalCommandPath, command, createBackup: true);
        EnsureUserPathEntry(_paths.UserBinRoot);
        return _paths.InstalledExePath;
    }

    public void LaunchOfficialClaudeInstaller()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -NoExit -Command \"irm https://claude.ai/install.ps1 | iex\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        });
    }

    private static void EnsureUserPathEntry(string requiredPath)
    {
        var current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !NormalizePath(entry).Equals(NormalizePath(requiredPath), StringComparison.OrdinalIgnoreCase))
            .ToList();
        entries.Add(requiredPath);
        Environment.SetEnvironmentVariable("Path", string.Join(';', entries), EnvironmentVariableTarget.User);
    }

    private static string NormalizePath(string path) =>
        Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar);

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("Windows Script Host 不可用，无法创建快捷方式。");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            var shortcutType = shortcut!.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [workingDirectory]);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["KClaude Desktop"]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}

public sealed class TerminalLauncher
{
    private readonly AppPaths _paths;
    private readonly ConfigurationStore _configuration;
    private readonly SecretStore _secrets;
    private readonly ClaudeCliLocator _locator;

    public TerminalLauncher(
        AppPaths paths,
        ConfigurationStore configuration,
        SecretStore secrets,
        ClaudeCliLocator locator)
    {
        _paths = paths;
        _configuration = configuration;
        _secrets = secrets;
        _locator = locator;
    }

    public Process Launch(
        string workingDirectory,
        ClaudeProfile profile,
        IReadOnlyList<string> arguments)
    {
        var claude = _locator.Find() ?? throw new FileNotFoundException("未找到 Claude Code CLI。");
        var config = _configuration.LoadSwitcherConfig();
        var definition = ProfileEnvironment.GetDefinition(profile, config, _paths);
        var key = _secrets.Load(profile);
        var helper = EnsureTerminalHelper();

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : _paths.UserProfile,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-NoExit");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(helper);
        startInfo.ArgumentList.Add(claude);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        ProfileEnvironment.Apply(startInfo, definition, key);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法打开 Claude Code 终端。");
        startInfo.Environment.Remove(definition.AuthenticationVariable);
        key = string.Empty;
        return process;
    }

    private string EnsureTerminalHelper()
    {
        _paths.EnsureDirectories();
        var path = Path.Combine(_paths.AppDataRoot, "terminal-host.ps1");
        const string script = """
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true, Position = 0)]
                [string]$ClaudeExecutable,
                [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
                [string[]]$ClaudeArguments = @()
            )
            Set-StrictMode -Version 2.0
            $ErrorActionPreference = 'Stop'
            & $ClaudeExecutable @ClaudeArguments
            $code = $LASTEXITCODE
            Write-Host ""
            Write-Host ("Claude Code exited with code {0}." -f $code)
            """;
        AtomicFile.WriteUtf8(path, script + Environment.NewLine, createBackup: false);
        return path;
    }
}
