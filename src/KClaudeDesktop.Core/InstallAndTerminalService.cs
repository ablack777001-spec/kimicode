using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace KClaudeDesktop.Core;

public sealed record UninstallPlan(
    string InstalledAppRoot,
    string InstalledExePath,
    string StartMenuShortcutPath,
    string DesktopShortcutPath,
    string TerminalCommandPath,
    string UserBinRoot,
    string UninstallRegistrySubKey,
    IReadOnlyList<string> OwnedFiles,
    IReadOnlyList<string> DataRoots);

public sealed record UninstallRegistration(
    string RegistrySubKey,
    IReadOnlyDictionary<string, object> Values);

public enum ExecutableDeploymentResult
{
    Installed,
    Updated,
    Unchanged
}

public interface IUserUninstallRegistry
{
    void Write(UninstallRegistration registration);
}

public sealed class WindowsUserUninstallRegistry : IUserUninstallRegistry
{
    public void Write(UninstallRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        using var key = Registry.CurrentUser.CreateSubKey(registration.RegistrySubKey, writable: true)
                        ?? throw new InvalidOperationException("无法创建当前用户卸载注册项。");
        foreach (var pair in registration.Values)
        {
            var kind = pair.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String;
            key.SetValue(pair.Key, pair.Value, kind);
        }
    }
}

public sealed class InstallService
{
    private readonly AppPaths _paths;
    private readonly IUserUninstallRegistry _uninstallRegistry;

    public InstallService(AppPaths paths, IUserUninstallRegistry? uninstallRegistry = null)
    {
        _paths = paths;
        _uninstallRegistry = uninstallRegistry ?? new WindowsUserUninstallRegistry();
    }

    public bool IsRunningSingleFile => IsSingleFileLayout(
        Environment.ProcessPath,
        Assembly.GetEntryAssembly()?.GetName().Name,
        File.Exists);

    public static bool IsSingleFileLayout(
        string? processPath,
        string? entryAssemblyName,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(entryAssemblyName))
        {
            return false;
        }

        var processName = Path.GetFileNameWithoutExtension(processPath);
        var allowedNames = new[]
        {
            entryAssemblyName,
            entryAssemblyName + "-unsigned",
            entryAssemblyName + "-Setup",
            entryAssemblyName + "-Setup-unsigned"
        };
        if (!allowedNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }
        return !fileExists(Path.Combine(directory, entryAssemblyName + ".dll"));
    }

    public UninstallPlan CreateUninstallPlan(bool purgeData)
    {
        ValidateOwnedInstallRoot(_paths.InstalledAppRoot, _paths.LocalAppDataRoot);
        var dataRoots = purgeData
            ? new[]
            {
                ValidateOwnedDataRoot(_paths.SwitcherRoot, _paths.UserProfile, ".claude-kimi-switch"),
                ValidateOwnedDataRoot(_paths.AppDataRoot, Path.GetDirectoryName(_paths.AppDataRoot)!, "KClaudeDesktop")
            }
            : Array.Empty<string>();
        return new UninstallPlan(
            Path.GetFullPath(_paths.InstalledAppRoot),
            Path.GetFullPath(_paths.InstalledExePath),
            Path.GetFullPath(_paths.StartMenuShortcutPath),
            Path.GetFullPath(_paths.DesktopShortcutPath),
            Path.GetFullPath(_paths.TerminalCommandPath),
            Path.GetFullPath(_paths.UserBinRoot),
            _paths.UninstallRegistrySubKey,
            new[]
            {
                Path.GetFullPath(_paths.StartMenuShortcutPath),
                Path.GetFullPath(_paths.DesktopShortcutPath),
                Path.GetFullPath(_paths.TerminalCommandPath)
            },
            dataRoots);
    }

    public UninstallRegistration CreateUninstallRegistration(string version, int estimatedSizeKb)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("版本不能为空。", nameof(version));
        }
        if (estimatedSizeKb < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedSizeKb));
        }
        var values = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["DisplayName"] = "KClaude Desktop",
            ["DisplayVersion"] = version,
            ["Publisher"] = "KClaude Desktop",
            ["InstallLocation"] = _paths.InstalledAppRoot,
            ["DisplayIcon"] = _paths.InstalledExePath,
            ["EstimatedSize"] = estimatedSizeKb,
            ["UninstallString"] = $"\"{_paths.InstalledExePath}\" --uninstall",
            ["NoModify"] = 1,
            ["NoRepair"] = 1,
            ["InstallDate"] = DateTime.Now.ToString("yyyyMMdd")
        };
        return new UninstallRegistration(_paths.UninstallRegistrySubKey, values);
    }

    public void RegisterCurrentUserInstall(string version, int estimatedSizeKb)
    {
        _uninstallRegistry.Write(CreateUninstallRegistration(version, estimatedSizeKb));
    }

    public static void ValidateOwnedInstallRoot(string installRoot, string localAppDataRoot)
    {
        var actual = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var expected = Path.GetFullPath(Path.Combine(localAppDataRoot, "Programs", "KClaude Desktop"))
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝清理不属于 KClaude Desktop 的安装目录。");
        }
    }

    public static string RemoveExactPathEntry(string currentPath, string pathToRemove)
    {
        var normalizedRequired = NormalizePath(pathToRemove);
        return string.Join(
            Path.PathSeparator,
            currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => !NormalizePath(entry).Equals(normalizedRequired, StringComparison.OrdinalIgnoreCase)));
    }

    public static string BuildCleanupScript(UninstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return """
            param(
                [Parameter(Mandatory=$true)][int]$ParentProcessId,
                [Parameter(Mandatory=$true)][string]$InstallRoot,
                [Parameter(Mandatory=$true)][string]$StartMenuShortcut,
                [Parameter(Mandatory=$true)][string]$DesktopShortcut,
                [Parameter(Mandatory=$true)][string]$TerminalCommand,
                [Parameter(Mandatory=$true)][string]$UserBinRoot,
                [Parameter(Mandatory=$true)][string]$UninstallRegistrySubKey,
                [string]$DataRootOne,
                [string]$DataRootTwo
            )
            $ErrorActionPreference = 'Stop'
            try {
                Wait-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
                foreach ($dataRoot in @($DataRootOne, $DataRootTwo)) {
                    if (-not [string]::IsNullOrWhiteSpace($dataRoot) -and (Test-Path -LiteralPath $dataRoot -PathType Container)) {
                        Remove-Item -LiteralPath $dataRoot -Recurse -Force
                    }
                }
                foreach ($ownedFile in @($StartMenuShortcut, $DesktopShortcut, $TerminalCommand)) {
                    if (-not [string]::IsNullOrWhiteSpace($ownedFile) -and (Test-Path -LiteralPath $ownedFile -PathType Leaf)) {
                        Remove-Item -LiteralPath $ownedFile -Force
                    }
                }
                $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
                $normalizedUserBinRoot = [IO.Path]::GetFullPath($UserBinRoot).TrimEnd('\')
                $updatedPath = [string]::Join(';', @($userPath -split ';' | Where-Object {
                    if ([string]::IsNullOrWhiteSpace($_)) { return $false }
                    try {
                        return -not ([IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($_.Trim().Trim('"'))).TrimEnd('\') -eq $normalizedUserBinRoot)
                    }
                    catch {
                        return $true
                    }
                }))
                [Environment]::SetEnvironmentVariable('Path', $updatedPath, 'User')
                if (Test-Path -LiteralPath $InstallRoot -PathType Container) {
                    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
                }
                $registryPath = 'Microsoft.PowerShell.Core\Registry::HKEY_CURRENT_USER\' + $UninstallRegistrySubKey
                if (Test-Path -LiteralPath $registryPath) {
                    Remove-Item -LiteralPath $registryPath -Recurse -Force
                }
                Remove-Item -LiteralPath $PSScriptRoot -Recurse -Force
            }
            catch {
                $_ | Out-String | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'cleanup-error.log') -Encoding UTF8
                exit 1
            }
            """;
    }

    public static ProcessStartInfo BuildCleanupStartInfo(
        string powerShellExecutable,
        string cleanupScriptPath,
        int parentProcessId,
        UninstallPlan plan)
    {
        if (string.IsNullOrWhiteSpace(powerShellExecutable))
        {
            throw new ArgumentException("PowerShell 路径不能为空。", nameof(powerShellExecutable));
        }
        if (parentProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parentProcessId));
        }
        ArgumentNullException.ThrowIfNull(plan);

        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath()
        };
        foreach (var argument in new[]
                 {
                     "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-File", cleanupScriptPath,
                     "-ParentProcessId", parentProcessId.ToString(),
                     "-InstallRoot", plan.InstalledAppRoot,
                     "-StartMenuShortcut", plan.StartMenuShortcutPath,
                     "-DesktopShortcut", plan.DesktopShortcutPath,
                     "-TerminalCommand", plan.TerminalCommandPath,
                     "-UserBinRoot", plan.UserBinRoot,
                     "-UninstallRegistrySubKey", plan.UninstallRegistrySubKey,
                     "-DataRootOne", plan.DataRoots.ElementAtOrDefault(0) ?? string.Empty,
                     "-DataRootTwo", plan.DataRoots.ElementAtOrDefault(1) ?? string.Empty
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment.Remove("ANTHROPIC_API_KEY");
        startInfo.Environment.Remove("ANTHROPIC_AUTH_TOKEN");
        return startInfo;
    }

    public static ExecutableDeploymentResult DeployExecutable(string sourcePath, string destinationPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("安装源 EXE 不存在。", source);
        }
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            return ExecutableDeploymentResult.Unchanged;
        }

        var destinationDirectory = Path.GetDirectoryName(destination)
                                   ?? throw new InvalidOperationException("安装目标目录无效。");
        Directory.CreateDirectory(destinationDirectory);
        var destinationExists = File.Exists(destination);
        if (destinationExists && FilesHaveSameContent(source, destination))
        {
            return ExecutableDeploymentResult.Unchanged;
        }

        if (destinationExists)
        {
            var backup = Path.Combine(
                destinationDirectory,
                $"KClaudeDesktop.exe.{DateTime.Now:yyyyMMdd-HHmmssfff}.{Guid.NewGuid():N}.bak");
            File.Copy(destination, backup, overwrite: false);
        }

        var temporary = Path.Combine(destinationDirectory, $"KClaudeDesktop.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        return destinationExists ? ExecutableDeploymentResult.Updated : ExecutableDeploymentResult.Installed;
    }

    private static string ValidateOwnedDataRoot(string path, string parentRoot, string expectedLeaf)
    {
        var actual = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var expected = Path.GetFullPath(Path.Combine(parentRoot, expectedLeaf)).TrimEnd(Path.DirectorySeparatorChar);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"拒绝清理不属于 KClaude Desktop 的数据目录: {path}");
        }
        return actual;
    }

    private static bool FilesHaveSameContent(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }
        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);
        return SHA256.HashData(first).SequenceEqual(SHA256.HashData(second));
    }

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

        DeployExecutable(source, _paths.InstalledExePath);

        CreateShortcut(_paths.StartMenuShortcutPath, _paths.InstalledExePath, _paths.InstalledAppRoot);
        if (createDesktopShortcut)
        {
            CreateShortcut(_paths.DesktopShortcutPath, _paths.InstalledExePath, _paths.InstalledAppRoot);
        }

        Directory.CreateDirectory(_paths.UserBinRoot);
        var command = $"@echo off\r\nstart \"\" /wait \"{_paths.InstalledExePath}\" --terminal %*\r\nexit /b %ERRORLEVEL%\r\n";
        AtomicFile.WriteUtf8(_paths.TerminalCommandPath, command, createBackup: true);
        EnsureUserPathEntry(_paths.UserBinRoot);
        var installedFile = new FileInfo(_paths.InstalledExePath);
        var versionInfo = FileVersionInfo.GetVersionInfo(_paths.InstalledExePath);
        var version = versionInfo.ProductVersion ?? versionInfo.FileVersion ?? "0.0.0";
        var estimatedSizeKb = checked((int)Math.Ceiling(installedFile.Length / 1024d));
        RegisterCurrentUserInstall(version, estimatedSizeKb);
        return _paths.InstalledExePath;
    }

    public string ScheduleUninstall(bool purgeData)
    {
        var plan = CreateUninstallPlan(purgeData);
        var cleanupRoot = Path.Combine(
            Path.GetTempPath(),
            "KClaudeDesktop.Uninstall",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cleanupRoot);
        try
        {
            TightenDirectoryAcl(cleanupRoot);
            var cleanupScriptPath = Path.Combine(cleanupRoot, "cleanup.ps1");
            AtomicFile.WriteUtf8(cleanupScriptPath, BuildCleanupScript(plan), createBackup: false);
            var startInfo = BuildCleanupStartInfo(
                FindPowerShellExecutable(),
                cleanupScriptPath,
                Environment.ProcessId,
                plan);
            var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("无法启动卸载清理进程。");
            }
            return cleanupScriptPath;
        }
        catch (Exception exception)
        {
            try
            {
                if (Directory.Exists(cleanupRoot))
                {
                    Directory.Delete(cleanupRoot, recursive: true);
                }
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException("无法启动卸载，且临时目录清理失败。", exception, cleanupException);
            }
            throw;
        }
    }

    public void LaunchOfficialClaudeInstaller()
    {
        var process = Process.Start(BuildClaudeInstallerStartInfo(FindWingetExecutable()));
        if (process is null)
        {
            throw new InvalidOperationException("无法启动 WinGet，也无法打开 Anthropic 官方安装说明。");
        }
    }

    public static ProcessStartInfo BuildClaudeInstallerStartInfo(string? wingetExecutable)
    {
        if (string.IsNullOrWhiteSpace(wingetExecutable))
        {
            return new ProcessStartInfo
            {
                FileName = "https://code.claude.com/docs/en/installation",
                UseShellExecute = true
            };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = wingetExecutable,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        foreach (var argument in new[]
                 {
                     "install",
                     "--id", "Anthropic.ClaudeCode",
                     "--exact",
                     "--source", "winget",
                     "--accept-package-agreements",
                     "--accept-source-agreements"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static string? FindWingetExecutable()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path.Trim('"'), "winget.exe"));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return paths
            .Prepend(Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static string FindPowerShellExecutable()
    {
        var candidates = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(directory.Trim('"'), "pwsh.exe"));
            candidates.Add(Path.Combine(directory.Trim('"'), "powershell.exe"));
        }
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"));
        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException("未找到 PowerShell，无法执行卸载清理。");
    }

    private static void TightenDirectoryAcl(string directory)
    {
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "icacls.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(directory);
        process.StartInfo.ArgumentList.Add("/inheritance:r");
        process.StartInfo.ArgumentList.Add("/grant:r");
        process.StartInfo.ArgumentList.Add($"{identity}:(OI)(CI)(F)");
        process.Start();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("无法收紧卸载临时目录 ACL。");
        }
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

        var startInfo = BuildConsoleStartInfo(
            Directory.Exists(workingDirectory) ? workingDirectory : _paths.UserProfile,
            claude,
            arguments);
        ProfileEnvironment.Apply(startInfo, definition, key);

        var process = StartInNewConsole(startInfo);
        startInfo.Environment.Remove(definition.AuthenticationVariable);
        key = string.Empty;
        return process;
    }

    public static ProcessStartInfo BuildConsoleStartInfo(
        string workingDirectory,
        string claudeExecutable,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = claudeExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    public static string BuildWindowsCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var parts = new List<string> { QuoteWindowsArgument(executable) };
        parts.AddRange(arguments.Select(QuoteWindowsArgument));
        return string.Join(' ', parts);
    }

    private static Process StartInNewConsole(ProcessStartInfo startInfo)
    {
        const uint createNewConsole = 0x00000010;
        const uint createUnicodeEnvironment = 0x00000400;
        var commandLine = new StringBuilder(BuildWindowsCommandLine(startInfo.FileName, startInfo.ArgumentList));
        var environmentBlock = string.Join(
            '\0',
            startInfo.Environment
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        var environmentPointer = Marshal.StringToHGlobalUni(environmentBlock);
        var startupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        try
        {
            if (!CreateProcess(
                    startInfo.FileName,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    createNewConsole | createUnicodeEnvironment,
                    environmentPointer,
                    startInfo.WorkingDirectory,
                    ref startupInfo,
                    out var processInfo))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法打开 Claude Code 终端。");
            }

            try
            {
                return Process.GetProcessById(unchecked((int)processInfo.ProcessId));
            }
            finally
            {
                CloseHandle(processInfo.ThreadHandle);
                CloseHandle(processInfo.ProcessHandle);
            }
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(environmentPointer);
        }
    }

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 &&
            !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(character);
        }
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
