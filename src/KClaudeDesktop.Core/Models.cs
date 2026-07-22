using System.Text.Json.Serialization;

namespace KClaudeDesktop.Core;

public enum ClaudeProfile
{
    Member,
    Api
}

public sealed class SwitcherConfig
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("defaultProfile")]
    public string DefaultProfile { get; set; } = "member";

    [JsonPropertyName("memberContext")]
    public string MemberContext { get; set; } = "1m";
}

public sealed class UiSettings
{
    public string LastProjectDirectory { get; set; } = "";
    public string LastProfile { get; set; } = "member";
    public string PermissionMode { get; set; } = "plan";
    public bool ContinueMostRecent { get; set; }
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public string LastKnownLatestVersion { get; set; } = "";
}

public sealed record SecretStatus(bool Exists, bool CanDecrypt, int Length, string Message);

public sealed record ClaudeProfileDefinition(
    ClaudeProfile Profile,
    string DisplayName,
    string BaseUrl,
    string Model,
    string ContextTokens,
    string Effort,
    string AuthenticationVariable,
    string SecretPath);

public sealed record ClaudeRunRequest(
    string ProjectDirectory,
    ClaudeProfile Profile,
    string PermissionMode,
    string Prompt,
    string SessionId,
    bool SessionStarted,
    bool ContinueMostRecent);

public enum ClaudeOutputKind
{
    Text,
    Status,
    Tool,
    Error
}

public sealed record ClaudeOutput(ClaudeOutputKind Kind, string Text);

public sealed record ClaudeRunResult(int ExitCode, bool SessionStarted, string SessionId);

public sealed record DiagnosticItem(string Status, string Name, string Detail);

public sealed record ClaudeCliBackup(
    string ExecutablePath,
    string MetadataPath,
    string Version,
    string Sha256,
    DateTimeOffset CreatedAt);

public sealed record ClaudeUpdateResult(
    bool Success,
    string PreviousVersion,
    string CurrentVersion,
    ClaudeCliBackup Backup,
    string Message);

public sealed record ClaudeUpdateCheck(
    string CurrentVersion,
    string LatestVersion,
    bool UpdateAvailable,
    DateTimeOffset CheckedAt,
    string Message,
    string ManifestCommit,
    DateTimeOffset? BuildDate,
    string ReleaseNotes,
    string ReleaseNotesUrl);
