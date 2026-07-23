using System.Diagnostics;
using System.Security;
using System.Text.Json;
using KClaudeDesktop.Core;

if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty)
    .Equals("claude", StringComparison.OrdinalIgnoreCase))
{
    return await RunFakeClaudeAsync(args);
}

var failures = new List<string>();
var passed = 0;

await Run("DPAPI round trip", () =>
{
    using var secure = ToSecureString("test-only-not-a-real-key-1234567890");
    var cipher = DpapiService.Protect(secure);
    Equal("test-only-not-a-real-key-1234567890", DpapiService.Unprotect(cipher));
    return Task.CompletedTask;
});

await Run("PowerShell DPAPI compatibility", async () =>
{
    const string dummy = "test-only-not-a-real-key-abcdefghijk";
    var fromPowerShell = await RunPowerShellAsync(
        "Import-Module Microsoft.PowerShell.Security -ErrorAction Stop; $v=[Console]::In.ReadToEnd(); ConvertFrom-SecureString (ConvertTo-SecureString $v -AsPlainText -Force)",
        dummy);
    Equal(dummy, DpapiService.Unprotect(fromPowerShell.Trim()));

    using var secure = ToSecureString(dummy);
    var fromDotNet = DpapiService.Protect(secure);
    var length = await RunPowerShellAsync(
        "Import-Module Microsoft.PowerShell.Security -ErrorAction Stop; $v=[Console]::In.ReadToEnd(); $s=$v|ConvertTo-SecureString; $b=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($s); try{[Runtime.InteropServices.Marshal]::PtrToStringBSTR($b).Length}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b)}",
        fromDotNet);
    Equal(dummy.Length.ToString(), length.Trim());
});

await Run("Profile authentication isolation", () =>
{
    var root = CreateTestRoot();
    var paths = new AppPaths(root, Path.Combine(root, "switch"), Path.Combine(root, "app"));
    var config = new SwitcherConfig();

    var memberInfo = new ProcessStartInfo();
    memberInfo.Environment["ANTHROPIC_API_KEY"] = "old";
    memberInfo.Environment["ANTHROPIC_AUTH_TOKEN"] = "old";
    ProfileEnvironment.Apply(
        memberInfo,
        ProfileEnvironment.GetDefinition(ClaudeProfile.Member, config, paths),
        "member-test-key");
    True(memberInfo.Environment.ContainsKey("ANTHROPIC_API_KEY"));
    False(memberInfo.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));

    var apiInfo = new ProcessStartInfo();
    apiInfo.Environment["ANTHROPIC_API_KEY"] = "old";
    apiInfo.Environment["ANTHROPIC_AUTH_TOKEN"] = "old";
    ProfileEnvironment.Apply(
        apiInfo,
        ProfileEnvironment.GetDefinition(ClaudeProfile.Api, config, paths),
        "api-test-key");
    False(apiInfo.Environment.ContainsKey("ANTHROPIC_API_KEY"));
    True(apiInfo.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
    DeleteTestRoot(root);
    return Task.CompletedTask;
});

await Run("Billing profile restores safely and requires confirmation", () =>
{
    Equal(ClaudeProfile.Member, BillingSafety.ResolveStartupProfile("api"));
    Equal(ClaudeProfile.Member, BillingSafety.ResolveStartupProfile("member"));
    Equal("member", BillingSafety.GetPersistedProfile(ClaudeProfile.Api));
    True(BillingSafety.RequiresApiConfirmation(ClaudeProfile.Api, alreadyConfirmed: false));
    False(BillingSafety.RequiresApiConfirmation(ClaudeProfile.Api, alreadyConfirmed: true));
    False(BillingSafety.RequiresApiConfirmation(ClaudeProfile.Member, alreadyConfirmed: false));
    return Task.CompletedTask;
});

await Run("Permission modes match the supported Claude CLI contract", () =>
{
    var request = new ClaudeRunRequest(
        Environment.CurrentDirectory,
        ClaudeProfile.Member,
        "default",
        "prompt",
        Guid.NewGuid().ToString(),
        false,
        false);
    var arguments = ClaudeCliRunner.BuildArguments(request);
    True(arguments.Contains("default"));

    Throws<ArgumentException>(() => ClaudeCliRunner.BuildArguments(request with { PermissionMode = "manual" }));
    Throws<ArgumentException>(() => ClaudeCliRunner.BuildArguments(request with { PermissionMode = "auto" }));
    return Task.CompletedTask;
});

await Run("Interactive terminal launches Claude directly without a credential-bearing shell", () =>
{
    var startInfo = TerminalLauncher.BuildConsoleStartInfo(
        Environment.CurrentDirectory,
        "claude.exe",
        ["-c"]);
    Equal("claude.exe", startInfo.FileName);
    Equal(Environment.CurrentDirectory, startInfo.WorkingDirectory);
    False(startInfo.UseShellExecute);
    Equal(1, startInfo.ArgumentList.Count);
    Equal("-c", startInfo.ArgumentList[0]);
    Equal(
        "\"C:\\Program Files\\claude.exe\" -c \"two words\" \"\"",
        TerminalLauncher.BuildWindowsCommandLine(
            @"C:\Program Files\claude.exe",
            ["-c", "two words", ""]));
    return Task.CompletedTask;
});

await Run("Configuration repair preserves user environment variables", () =>
{
    var root = CreateTestRoot();
    var paths = new AppPaths(root, Path.Combine(root, "switch"), Path.Combine(root, "app"));
    var service = new ClaudeConfigurationService(
        paths,
        name => name == "ANTHROPIC_API_KEY" ? "present" : null);
    var changes = service.InitializeAndRepair();
    True(changes.Any(change => change.Contains("已保留用户环境变量: ANTHROPIC_API_KEY", StringComparison.Ordinal)));
    DeleteTestRoot(root);
    return Task.CompletedTask;
});

await Run("Canceled managed process is terminated", async () =>
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    process.StartInfo.ArgumentList.Add("-NoLogo");
    process.StartInfo.ArgumentList.Add("-NoProfile");
    process.StartInfo.ArgumentList.Add("-Command");
    process.StartInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
    process.Start();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    await ThrowsAsync<OperationCanceledException>(() =>
        ManagedProcess.WaitForExitOrKillAsync(process, [], cancellation.Token));
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    True(process.HasExited);
});

await Run("Single-file layout detection does not use Assembly.Location", () =>
{
    False(InstallService.IsSingleFileLayout(
        @"C:\app\KClaudeDesktop.exe",
        "KClaudeDesktop",
        path => path.EndsWith("KClaudeDesktop.dll", StringComparison.OrdinalIgnoreCase)));
    True(InstallService.IsSingleFileLayout(
        @"C:\app\KClaudeDesktop.exe",
        "KClaudeDesktop",
        _ => false));
    True(InstallService.IsSingleFileLayout(
        @"C:\app\KClaudeDesktop-unsigned.exe",
        "KClaudeDesktop",
        _ => false));
    True(InstallService.IsSingleFileLayout(
        @"C:\app\KClaudeDesktop-Setup.exe",
        "KClaudeDesktop",
        _ => false));
    True(InstallService.IsSingleFileLayout(
        @"C:\app\KClaudeDesktop-Setup-unsigned.exe",
        "KClaudeDesktop",
        _ => false));
    False(InstallService.IsSingleFileLayout(
        @"C:\app\KClaudeDesktop-copy.exe",
        "KClaudeDesktop",
        _ => false));
    False(InstallService.IsSingleFileLayout(
        @"C:\Program Files\dotnet\dotnet.exe",
        "KClaudeDesktop",
        _ => false));
    return Task.CompletedTask;
});

await Run("Claude installer uses the package manager without a remote script shell", () =>
{
    var startInfo = InstallService.BuildClaudeInstallerStartInfo("winget.exe");
    Equal("winget.exe", startInfo.FileName);
    True(startInfo.UseShellExecute);
    True(startInfo.ArgumentList.Contains("Anthropic.ClaudeCode"));
    False(startInfo.ArgumentList.Any(argument => argument.Contains("install.ps1", StringComparison.OrdinalIgnoreCase)));
    False(startInfo.ArgumentList.Any(argument => argument.Equals("iex", StringComparison.OrdinalIgnoreCase)));

    var fallback = InstallService.BuildClaudeInstallerStartInfo(null);
    Equal("https://code.claude.com/docs/en/installation", fallback.FileName);
    True(fallback.UseShellExecute);
    Equal(0, fallback.ArgumentList.Count);
    return Task.CompletedTask;
});

await Run("Uninstall registration and cleanup stay within application-owned roots", () =>
{
    var root = CreateTestRoot();
    var userProfile = Path.Combine(root, "user");
    var localAppData = Path.Combine(root, "local");
    var startMenu = Path.Combine(root, "start-menu");
    var desktop = Path.Combine(root, "desktop");
    var paths = new AppPaths(
        userProfile,
        Path.Combine(userProfile, ".claude-kimi-switch"),
        Path.Combine(root, "roaming", "KClaudeDesktop"),
        localAppData,
        startMenu,
        desktop);
    var installer = new InstallService(paths);

    var preservePlan = installer.CreateUninstallPlan(purgeData: false);
    Equal(Path.Combine(localAppData, "Programs", "KClaude Desktop"), preservePlan.InstalledAppRoot);
    Equal(0, preservePlan.DataRoots.Count);
    False(preservePlan.OwnedFiles.Any(path => path.Contains(".claude.json", StringComparison.OrdinalIgnoreCase)));
    False(preservePlan.OwnedFiles.Any(path => path.Contains(Path.Combine(".claude", "settings.json"), StringComparison.OrdinalIgnoreCase)));

    var purgePlan = installer.CreateUninstallPlan(purgeData: true);
    Equal(2, purgePlan.DataRoots.Count);
    True(purgePlan.DataRoots.Contains(paths.SwitcherRoot));
    True(purgePlan.DataRoots.Contains(paths.AppDataRoot));
    var cleanupScript = InstallService.BuildCleanupScript(purgePlan);
    True(cleanupScript.Contains("Remove-Item -LiteralPath", StringComparison.Ordinal));
    False(cleanupScript.Contains(".claude.json", StringComparison.OrdinalIgnoreCase));
    True(cleanupScript.Contains("cleanup-error.log", StringComparison.Ordinal));
    True(
        cleanupScript.IndexOf("foreach ($dataRoot", StringComparison.Ordinal) <
        cleanupScript.IndexOf("if (Test-Path -LiteralPath $InstallRoot", StringComparison.Ordinal));

    var registration = installer.CreateUninstallRegistration("1.1.0", estimatedSizeKb: 64000);
    Equal("KClaude Desktop", (string)registration.Values["DisplayName"]);
    Equal("1.1.0", (string)registration.Values["DisplayVersion"]);
    Equal(1, (int)registration.Values["NoModify"]);
    Equal(1, (int)registration.Values["NoRepair"]);
    Equal($"\"{paths.InstalledExePath}\" --uninstall", (string)registration.Values["UninstallString"]);

    Equal(
        string.Join(';', new[] { "C:\\Tools", "C:\\Other" }),
        InstallService.RemoveExactPathEntry(
            string.Join(';', new[] { "C:\\Tools", paths.UserBinRoot, "C:\\Other", paths.UserBinRoot }),
            paths.UserBinRoot));
    DeleteTestRoot(root);
    return Task.CompletedTask;
});

await Run("Uninstall plan rejects a broad install root", () =>
{
    var root = CreateTestRoot();
    Throws<InvalidOperationException>(() => InstallService.ValidateOwnedInstallRoot(root, root));
    DeleteTestRoot(root);
    return Task.CompletedTask;
});

await Run("Repeated installs update one uninstall registration", () =>
{
    var root = CreateTestRoot();
    var registry = new RecordingUninstallRegistry();
    var paths = new AppPaths(
        Path.Combine(root, "user"),
        Path.Combine(root, "user", ".claude-kimi-switch"),
        Path.Combine(root, "roaming", "KClaudeDesktop"),
        Path.Combine(root, "local"),
        Path.Combine(root, "start-menu"),
        Path.Combine(root, "desktop"));
    var installer = new InstallService(paths, registry);
    installer.RegisterCurrentUserInstall("1.0.0", estimatedSizeKb: 63000);
    installer.RegisterCurrentUserInstall("1.1.0", estimatedSizeKb: 64000);
    Equal(1, registry.Entries.Count);
    Equal("1.1.0", (string)registry.Entries[paths.UninstallRegistrySubKey].Values["DisplayVersion"]);
    Equal(64000, (int)registry.Entries[paths.UninstallRegistrySubKey].Values["EstimatedSize"]);
    DeleteTestRoot(root);
    return Task.CompletedTask;
});

await Run("Executable deployment is idempotent and keeps one verified previous binary", () =>
{
    var root = CreateTestRoot();
    var source = Path.Combine(root, "source.exe");
    var destination = Path.Combine(root, "installed", "KClaudeDesktop.exe");
    File.WriteAllText(source, "version-one");

    Equal(ExecutableDeploymentResult.Installed, InstallService.DeployExecutable(source, destination));
    Equal("version-one", File.ReadAllText(destination));
    Equal(ExecutableDeploymentResult.Unchanged, InstallService.DeployExecutable(source, destination));
    Equal(0, Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.bak").Length);

    File.WriteAllText(source, "version-two");
    Equal(ExecutableDeploymentResult.Updated, InstallService.DeployExecutable(source, destination));
    Equal("version-two", File.ReadAllText(destination));
    var backups = Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.bak");
    Equal(1, backups.Length);
    Equal("version-one", File.ReadAllText(backups[0]));
    DeleteTestRoot(root);
    return Task.CompletedTask;
});

await Run("Uninstall helper receives validated literal paths without a visible shell", () =>
{
    var root = CreateTestRoot();
    var paths = new AppPaths(
        Path.Combine(root, "user"),
        Path.Combine(root, "user", ".claude-kimi-switch"),
        Path.Combine(root, "roaming", "KClaudeDesktop"),
        Path.Combine(root, "local"),
        Path.Combine(root, "start-menu"),
        Path.Combine(root, "desktop"));
    var plan = new InstallService(paths).CreateUninstallPlan(purgeData: true);
    var startInfo = InstallService.BuildCleanupStartInfo(
        "pwsh.exe",
        Path.Combine(root, "cleanup.ps1"),
        parentProcessId: 42,
        plan);
    Equal("pwsh.exe", startInfo.FileName);
    False(startInfo.UseShellExecute);
    True(startInfo.CreateNoWindow);
    Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
    True(startInfo.ArgumentList.Contains(paths.InstalledAppRoot));
    True(startInfo.ArgumentList.Contains(paths.SwitcherRoot));
    True(startInfo.ArgumentList.Contains(paths.AppDataRoot));
    False(startInfo.ArgumentList.Any(argument => argument.Contains(".claude.json", StringComparison.OrdinalIgnoreCase)));
    DeleteTestRoot(root);
    return Task.CompletedTask;
});

await Run("Application startup recognizes only the explicit uninstall command", () =>
{
    Equal(ApplicationStartupMode.Uninstall, ApplicationStartup.Resolve(["--uninstall"]));
    Equal(ApplicationStartupMode.Normal, ApplicationStartup.Resolve([]));
    Equal(ApplicationStartupMode.Normal, ApplicationStartup.Resolve(["--terminal", "member"]));
    Equal(ApplicationStartupMode.Normal, ApplicationStartup.Resolve(["uninstall"]));
    return Task.CompletedTask;
});

await Run("Fake Claude integration verifies isolation, streaming, diagnostics, and cancellation", async () =>
{
    var root = CreateTestRoot();
    var originalPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        var fakeCliRoot = Path.Combine(root, "fake-cli");
        var fakeClaude = CreateFakeClaudeExecutable(fakeCliRoot);
        Environment.SetEnvironmentVariable("PATH", fakeCliRoot + Path.PathSeparator + originalPath);

        var paths = new AppPaths(
            Path.Combine(root, "user"),
            Path.Combine(root, "user", ".claude-kimi-switch"),
            Path.Combine(root, "roaming", "KClaudeDesktop"),
            Path.Combine(root, "local"),
            Path.Combine(root, "start-menu"),
            Path.Combine(root, "desktop"));
        var configuration = new ConfigurationStore(paths);
        configuration.SaveSwitcherConfig(new SwitcherConfig());
        var secrets = new SecretStore(paths);
        using (var memberKey = ToSecureString("member-test-only-key-123456789"))
        {
            secrets.Save(ClaudeProfile.Member, memberKey);
        }
        using (var apiKey = ToSecureString("api-test-only-key-123456789012"))
        {
            secrets.Save(ClaudeProfile.Api, apiKey);
        }

        var locator = new ClaudeCliLocator(paths);
        Equal(fakeClaude, locator.Find());
        var verification = await locator.VerifyAsync();
        True(verification.Success);
        var capabilities = await locator.CheckGuiCapabilitiesAsync();
        True(capabilities.Success);

        var runner = new ClaudeCliRunner(paths, configuration, secrets, locator);
        var memberOutput = new List<ClaudeOutput>();
        var memberRequest = new ClaudeRunRequest(
            root,
            ClaudeProfile.Member,
            "plan",
            "integration-member",
            Guid.NewGuid().ToString(),
            false,
            false);
        var memberResult = await runner.RunAsync(memberRequest, memberOutput.Add, CancellationToken.None);
        Equal(0, memberResult.ExitCode);
        True(memberResult.SessionStarted);
        True(memberOutput.Any(item => item.Text.Contains("fake-member:integration-member", StringComparison.Ordinal)));

        var apiOutput = new List<ClaudeOutput>();
        var apiRequest = memberRequest with
        {
            Profile = ClaudeProfile.Api,
            PermissionMode = "default",
            Prompt = "integration-api",
            SessionId = Guid.NewGuid().ToString()
        };
        var apiResult = await runner.RunAsync(apiRequest, apiOutput.Add, CancellationToken.None);
        Equal(0, apiResult.ExitCode);
        True(apiResult.SessionStarted);
        True(apiOutput.Any(item => item.Text.Contains("fake-api:integration-api", StringComparison.Ordinal)));

        var diagnostics = await new DiagnosticsService(paths, configuration, secrets, locator).RunAsync();
        Equal("PASS", diagnostics.Single(item => item.Name == "Claude Code CLI").Status);
        Equal("PASS", diagnostics.Single(item => item.Name == "GUI 兼容能力").Status);
        Equal("PASS", diagnostics.Single(item => item.Name == "鉴权互斥").Status);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
            memberRequest with { Prompt = "__cancel__", SessionId = Guid.NewGuid().ToString() },
            _ => { },
            cancellation.Token));
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", originalPath);
        DeleteTestRoot(root);
    }
});

await Run("Prompt and Key excluded from command arguments", () =>
{
    var request = new ClaudeRunRequest(
        Environment.CurrentDirectory,
        ClaudeProfile.Member,
        "plan",
        "sensitive prompt must use stdin",
        Guid.NewGuid().ToString(),
        false,
        false);
    var arguments = ClaudeCliRunner.BuildArguments(request);
    False(arguments.Any(argument => argument.Contains("sensitive prompt", StringComparison.Ordinal)));
    False(arguments.Any(argument => argument.Contains("key", StringComparison.OrdinalIgnoreCase)));
    True(arguments.Contains("stream-json"));
    True(arguments.Contains("--session-id"));
    return Task.CompletedTask;
});

await Run("Session resume argument", () =>
{
    var session = Guid.NewGuid().ToString();
    var request = new ClaudeRunRequest(
        Environment.CurrentDirectory,
        ClaudeProfile.Member,
        "acceptEdits",
        "prompt",
        session,
        true,
        false);
    var arguments = ClaudeCliRunner.BuildArguments(request);
    True(arguments.Contains("--resume"));
    True(arguments.Contains(session));
    False(arguments.Contains("--session-id"));
    return Task.CompletedTask;
});

await Run("Stream JSON parser", () =>
{
    const string line = "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}}";
    var output = ClaudeStreamParser.Parse(line, false);
    Equal(1, output.Count);
    Equal(ClaudeOutputKind.Text, output[0].Kind);
    Equal("hello", output[0].Text);
    return Task.CompletedTask;
});

await Run("Configuration contains no Key", () =>
{
    var root = CreateTestRoot();
    var paths = new AppPaths(root, Path.Combine(root, "switch"), Path.Combine(root, "app"));
    var store = new ConfigurationStore(paths);
    store.SaveSwitcherConfig(new SwitcherConfig { MemberContext = "256k" });
    var text = File.ReadAllText(paths.ConfigPath);
    False(text.Contains("API_KEY", StringComparison.OrdinalIgnoreCase));
    False(text.Contains("AUTH_TOKEN", StringComparison.OrdinalIgnoreCase));
    Equal("member", store.LoadSwitcherConfig().DefaultProfile);
    DeleteTestRoot(root);
    return Task.CompletedTask;
});

await Run("Existing user DPAPI files remain compatible", () =>
{
    var paths = new AppPaths();
    var store = new SecretStore(paths);
    var member = store.GetStatus(ClaudeProfile.Member);
    var api = store.GetStatus(ClaudeProfile.Api);
    if (member.Exists)
    {
        True(member.CanDecrypt);
    }
    if (api.Exists)
    {
        True(api.CanDecrypt);
    }
    return Task.CompletedTask;
});

if (args.Contains("--online", StringComparer.OrdinalIgnoreCase))
{
    await Run("Official update metadata and release notes", async () =>
    {
        var paths = new AppPaths();
        var locator = new ClaudeCliLocator(paths);
        var updater = new ClaudeUpdateService(paths, locator);
        var update = await updater.CheckForUpdateAsync();
        True(Version.TryParse(update.CurrentVersion, out _));
        True(Version.TryParse(update.LatestVersion, out _));
        if (!string.IsNullOrWhiteSpace(update.ReleaseNotes))
        {
            True(update.ReleaseNotesUrl.StartsWith("https://github.com/anthropics/claude-code/", StringComparison.OrdinalIgnoreCase));
        }
    });
}

Console.WriteLine();
Console.WriteLine($"Smoke tests: PASS={passed} FAIL={failures.Count}");
foreach (var failure in failures)
{
    Console.WriteLine("FAIL  " + failure);
}
return failures.Count == 0 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    try
    {
        await test();
        passed++;
        Console.WriteLine("PASS  " + name);
    }
    catch (Exception exception)
    {
        failures.Add(name + ": " + exception.Message);
    }
}

static SecureString ToSecureString(string value)
{
    var secure = new SecureString();
    foreach (var character in value)
    {
        secure.AppendChar(character);
    }
    secure.MakeReadOnly();
    return secure;
}

static async Task<string> RunPowerShellAsync(string script, string stdin)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };
    process.StartInfo.ArgumentList.Add("-NoLogo");
    process.StartInfo.ArgumentList.Add("-NoProfile");
    process.StartInfo.ArgumentList.Add("-Command");
    process.StartInfo.ArgumentList.Add(script);
    process.StartInfo.Environment.Remove("PSModulePath");
    process.Start();
    await process.StandardInput.WriteAsync(stdin);
    process.StandardInput.Close();
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException((await errorTask).Trim());
    }
    return await outputTask;
}

static async Task<int> RunFakeClaudeAsync(string[] arguments)
{
    if (arguments.Contains("--version", StringComparer.Ordinal))
    {
        Console.WriteLine("9.9.9-test (Claude Code fake)");
        return 0;
    }
    if (arguments.Contains("--help", StringComparer.Ordinal))
    {
        Console.WriteLine("--print --output-format stream-json --session-id --resume --permission-mode");
        return 0;
    }
    if (!arguments.Contains("--print", StringComparer.Ordinal))
    {
        Console.Error.WriteLine("fake Claude only supports test commands");
        return 2;
    }

    var memberPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
    var apiPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"));
    if (memberPresent == apiPresent)
    {
        Console.Error.WriteLine("authentication variables are not isolated");
        return 3;
    }

    var prompt = await Console.In.ReadToEndAsync();
    if (prompt == "__cancel__")
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 0;
    }

    var profile = memberPresent ? "member" : "api";
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        type = "stream_event",
        @event = new
        {
            type = "content_block_delta",
            delta = new { type = "text_delta", text = $"fake-{profile}:{prompt}" }
        }
    }));
    Console.WriteLine(JsonSerializer.Serialize(new { type = "result", is_error = false, result = "done" }));
    return 0;
}

static string CreateFakeClaudeExecutable(string destinationDirectory)
{
    Directory.CreateDirectory(destinationDirectory);
    var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate smoke test process.");
    var sourceDirectory = Path.GetDirectoryName(processPath)!;
    if (!InstallService.IsSingleFileLayout(processPath, "KClaudeDesktop.SmokeTests", File.Exists))
    {
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
        }
    }
    var fakeClaude = Path.Combine(destinationDirectory, "claude.exe");
    File.Copy(processPath, fakeClaude, overwrite: true);
    return fakeClaude;
}

static string CreateTestRoot()
{
    var path = Path.Combine(Path.GetTempPath(), "KClaudeDesktop.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void DeleteTestRoot(string path)
{
    var full = Path.GetFullPath(path);
    var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KClaudeDesktop.Tests")) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Refusing to remove a non-test directory.");
    }
    Directory.Delete(full, recursive: true);
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void False(bool value)
{
    if (value) throw new InvalidOperationException("Expected false.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class RecordingUninstallRegistry : IUserUninstallRegistry
{
    public Dictionary<string, UninstallRegistration> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Write(UninstallRegistration registration)
    {
        Entries[registration.RegistrySubKey] = registration;
    }
}
