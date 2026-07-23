using System.Diagnostics;

namespace KClaudeDesktop.Core;

public static class ProfileEnvironment
{
    public static readonly string[] ConflictingVariables =
    [
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_MODEL",
        "ANTHROPIC_SMALL_FAST_MODEL",
        "ANTHROPIC_DEFAULT_FABLE_MODEL",
        "ANTHROPIC_DEFAULT_FABLE_MODEL_NAME",
        "ANTHROPIC_DEFAULT_OPUS_MODEL",
        "ANTHROPIC_DEFAULT_OPUS_MODEL_NAME",
        "ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_SONNET_MODEL_NAME",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL_NAME",
        "CLAUDE_CODE_SUBAGENT_MODEL",
        "ENABLE_TOOL_SEARCH",
        "CLAUDE_CODE_AUTO_COMPACT_WINDOW",
        "CLAUDE_CODE_MAX_CONTEXT_TOKENS",
        "CLAUDE_CODE_EFFORT_LEVEL"
    ];

    public static ClaudeProfileDefinition GetDefinition(
        ClaudeProfile profile,
        SwitcherConfig config,
        AppPaths paths)
    {
        if (profile == ClaudeProfile.Member)
        {
            var oneMillion = config.MemberContext == "1m";
            return new ClaudeProfileDefinition(
                profile,
                oneMillion ? "会员 · K3 1M" : "会员 · K3 256K",
                "https://api.kimi.com/coding/",
                oneMillion ? "k3[1m]" : "k3",
                oneMillion ? "1048576" : "262144",
                "high",
                "ANTHROPIC_API_KEY",
                paths.MemberSecretPath);
        }

        return new ClaudeProfileDefinition(
            profile,
            "开放平台 · 按量计费",
            "https://api.moonshot.cn/anthropic",
            "kimi-k3[1m]",
            "1048576",
            "max",
            "ANTHROPIC_AUTH_TOKEN",
            paths.PlatformSecretPath);
    }

    public static void Apply(
        ProcessStartInfo startInfo,
        ClaudeProfileDefinition definition,
        string key)
    {
        foreach (var name in ConflictingVariables)
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["ANTHROPIC_BASE_URL"] = definition.BaseUrl;
        startInfo.Environment[definition.AuthenticationVariable] = key;
        startInfo.Environment["ANTHROPIC_MODEL"] = definition.Model;
        startInfo.Environment["ANTHROPIC_DEFAULT_FABLE_MODEL"] = definition.Model;
        startInfo.Environment["ANTHROPIC_DEFAULT_OPUS_MODEL"] = definition.Model;
        startInfo.Environment["ANTHROPIC_DEFAULT_SONNET_MODEL"] = definition.Model;
        startInfo.Environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = definition.Model;
        startInfo.Environment["CLAUDE_CODE_SUBAGENT_MODEL"] = definition.Model;
        startInfo.Environment["CLAUDE_CODE_EFFORT_LEVEL"] = definition.Effort;
        startInfo.Environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"] = definition.ContextTokens;
        startInfo.Environment["CLAUDE_CODE_MAX_CONTEXT_TOKENS"] = definition.ContextTokens;
        startInfo.Environment["ENABLE_TOOL_SEARCH"] = "false";

        if (startInfo.Environment.ContainsKey("ANTHROPIC_API_KEY") &&
            startInfo.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"))
        {
            throw new InvalidOperationException("安全检查失败：两个鉴权变量不能同时进入 Claude 进程。");
        }
    }
}

public static class BillingSafety
{
    public static ClaudeProfile ResolveStartupProfile(string? persistedProfile) => ClaudeProfile.Member;

    public static string GetPersistedProfile(ClaudeProfile selectedProfile) => "member";

    public static bool RequiresApiConfirmation(ClaudeProfile profile, bool alreadyConfirmed) =>
        profile == ClaudeProfile.Api && !alreadyConfirmed;
}
