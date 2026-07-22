using System.Diagnostics;
using System.Security;
using KClaudeDesktop.Core;

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
