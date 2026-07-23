using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KClaudeDesktop.Core;

public sealed class ClaudeConfigurationService
{
    private readonly AppPaths _paths;
    private readonly Func<string, string?> _readUserEnvironmentVariable;

    public ClaudeConfigurationService(
        AppPaths paths,
        Func<string, string?>? readUserEnvironmentVariable = null)
    {
        _paths = paths;
        _readUserEnvironmentVariable = readUserEnvironmentVariable ??
            (name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User));
    }

    public IReadOnlyList<string> InitializeAndRepair()
    {
        _paths.EnsureDirectories();
        var changes = new List<string>();

        var claudeObject = ReadObjectOrNew(_paths.ClaudeJsonPath);
        if (File.Exists(_paths.ClaudeJsonPath))
        {
            AtomicFile.Backup(_paths.ClaudeJsonPath, _paths.BackupsRoot);
        }
        claudeObject["penguinModeOrgEnabled"] = true;
        claudeObject["hasCompletedOnboarding"] = true;
        AtomicFile.WriteUtf8(
            _paths.ClaudeJsonPath,
            claudeObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            createBackup: false);
        changes.Add("已初始化 .claude.json 的第三方模型支持字段");

        var settingsObject = ReadObjectOrNew(_paths.ClaudeSettingsPath);
        if (File.Exists(_paths.ClaudeSettingsPath))
        {
            AtomicFile.Backup(_paths.ClaudeSettingsPath, _paths.BackupsRoot);
        }
        var environment = settingsObject["env"] as JsonObject ?? new JsonObject();
        settingsObject["env"] = environment;
        foreach (var name in ProfileEnvironment.ConflictingVariables)
        {
            if (environment.Remove(name))
            {
                changes.Add($"已清理 settings.json: {name}");
            }
        }

        var gitBash = FindGitBash();
        if (gitBash is not null)
        {
            environment["CLAUDE_CODE_GIT_BASH_PATH"] = gitBash;
            changes.Add($"已配置 Git Bash: {gitBash}");
        }
        AtomicFile.WriteUtf8(
            _paths.ClaudeSettingsPath,
            settingsObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            createBackup: false);

        foreach (var name in ProfileEnvironment.ConflictingVariables)
        {
            if (_readUserEnvironmentVariable(name) is not null)
            {
                changes.Add($"已保留用户环境变量: {name}（KClaude 子进程会隔离该变量）");
            }
        }

        return changes;
    }

    public static string? FindGitBash()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
            {
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files\Git\usr\bin\bash.exe",
                Path.Combine(local, "Programs", "Git", "bin", "bash.exe")
            }
            .FirstOrDefault(File.Exists);
    }

    private static JsonObject ReadObjectOrNew(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidDataException($"JSON 顶层不是对象：{path}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"JSON 非法，已保留且没有覆盖：{path}", exception);
        }
    }
}

public sealed class DiagnosticsService
{
    private readonly AppPaths _paths;
    private readonly ConfigurationStore _configuration;
    private readonly SecretStore _secrets;
    private readonly ClaudeCliLocator _locator;

    public DiagnosticsService(
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

    public async Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<DiagnosticItem>
        {
            new("PASS", "Windows", Environment.OSVersion.VersionString),
            new("PASS", ".NET", Environment.Version.ToString())
        };

        var cli = await _locator.VerifyAsync(cancellationToken);
        items.Add(new DiagnosticItem(cli.Success ? "PASS" : "FAIL", "Claude Code CLI", cli.Detail));
        if (cli.Success)
        {
            var capabilities = await _locator.CheckGuiCapabilitiesAsync(cancellationToken);
            items.Add(new DiagnosticItem(
                capabilities.Success ? "PASS" : "FAIL",
                "GUI 兼容能力",
                capabilities.Detail));
        }

        var member = _secrets.GetStatus(ClaudeProfile.Member);
        items.Add(new DiagnosticItem(
            member.CanDecrypt ? "PASS" : member.Exists ? "FAIL" : "WARN",
            "会员 Key",
            member.Message));

        var api = _secrets.GetStatus(ClaudeProfile.Api);
        items.Add(new DiagnosticItem(
            api.CanDecrypt ? "PASS" : api.Exists ? "FAIL" : "WARN",
            "开放平台 Key",
            api.Message));

        try
        {
            var config = _configuration.LoadSwitcherConfig();
            items.Add(new DiagnosticItem(
                config.DefaultProfile == "member" ? "PASS" : "FAIL",
                "默认通道",
                config.DefaultProfile));
            items.Add(new DiagnosticItem(
                config.MemberContext is "1m" or "256k" ? "PASS" : "FAIL",
                "会员上下文",
                config.MemberContext));

            var memberDefinition = ProfileEnvironment.GetDefinition(ClaudeProfile.Member, config, _paths);
            var apiDefinition = ProfileEnvironment.GetDefinition(ClaudeProfile.Api, config, _paths);
            var isolated = memberDefinition.AuthenticationVariable == "ANTHROPIC_API_KEY" &&
                           apiDefinition.AuthenticationVariable == "ANTHROPIC_AUTH_TOKEN";
            items.Add(new DiagnosticItem(
                isolated ? "PASS" : "FAIL",
                "鉴权互斥",
                isolated ? "会员仅 API_KEY；按量仅 AUTH_TOKEN" : "配置错误"));
        }
        catch (Exception exception)
        {
            items.Add(new DiagnosticItem("FAIL", "配置", exception.Message));
        }

        var conflicts = ProfileEnvironment.ConflictingVariables
            .Where(name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) is not null)
            .ToArray();
        items.Add(new DiagnosticItem(
            conflicts.Length == 0 ? "PASS" : "WARN",
            "用户环境变量",
            conflicts.Length == 0 ? "无冲突项" : string.Join(", ", conflicts)));

        var gitBash = ClaudeConfigurationService.FindGitBash();
        items.Add(new DiagnosticItem(gitBash is null ? "WARN" : "PASS", "Git Bash", gitBash ?? "未找到"));
        return items;
    }
}
