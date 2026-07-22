using System.Diagnostics;
using System.Text.Json;

namespace KClaudeDesktop.Core;

public sealed class ClaudeCliLocator
{
    private readonly AppPaths _paths;

    public ClaudeCliLocator(AppPaths paths)
    {
        _paths = paths;
    }

    public string? Find()
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in pathEntries)
        {
            var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(entry.Trim('"')), "claude.exe");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var native = Path.Combine(_paths.UserProfile, ".local", "bin", "claude.exe");
        if (File.Exists(native))
        {
            return native;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Claude", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anthropic", "Claude Code", "claude.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<(bool Success, string Detail)> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var executable = Find();
        if (executable is null)
        {
            return (false, "未找到 Claude Code CLI");
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("--version");
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            return process.ExitCode == 0
                ? (true, $"{output} · {executable}")
                : (false, string.IsNullOrWhiteSpace(error) ? "claude --version 失败" : error);
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    public async Task<(bool Success, string Detail)> CheckGuiCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var executable = Find();
        if (executable is null)
        {
            return (false, "未找到 Claude Code CLI");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--help");
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var help = (await outputTask) + Environment.NewLine + (await errorTask);
        var required = new[] { "--print", "stream-json", "--session-id", "--resume", "--permission-mode" };
        var missing = required.Where(flag => !help.Contains(flag, StringComparison.Ordinal)).ToArray();
        return process.ExitCode == 0 && missing.Length == 0
            ? (true, "GUI 所需 stream-json、会话和权限参数均受支持")
            : (false, missing.Length == 0 ? "claude --help 失败" : "缺少参数: " + string.Join(", ", missing));
    }
}

public sealed class ClaudeCliRunner
{
    private readonly AppPaths _paths;
    private readonly ConfigurationStore _configuration;
    private readonly SecretStore _secrets;
    private readonly ClaudeCliLocator _locator;
    private Process? _activeProcess;

    public ClaudeCliRunner(
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

    public static IReadOnlyList<string> BuildArguments(ClaudeRunRequest request)
    {
        var allowedModes = new[] { "plan", "manual", "acceptEdits", "auto", "dontAsk" };
        if (!allowedModes.Contains(request.PermissionMode, StringComparer.Ordinal))
        {
            throw new ArgumentException("不支持的权限模式。", nameof(request));
        }

        var arguments = new List<string>
        {
            "--print",
            "--verbose",
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--permission-mode", request.PermissionMode
        };

        if (request.ContinueMostRecent)
        {
            arguments.Add("--continue");
        }
        else if (request.SessionStarted)
        {
            arguments.Add("--resume");
            arguments.Add(request.SessionId);
        }
        else
        {
            arguments.Add("--session-id");
            arguments.Add(request.SessionId);
        }

        return arguments;
    }

    public async Task<ClaudeRunResult> RunAsync(
        ClaudeRunRequest request,
        Action<ClaudeOutput> onOutput,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.ProjectDirectory))
        {
            throw new DirectoryNotFoundException("项目目录不存在。");
        }
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("消息不能为空。", nameof(request));
        }

        var executable = _locator.Find() ?? throw new FileNotFoundException("未找到 Claude Code CLI，请先在设置中安装。");
        var config = _configuration.LoadSwitcherConfig();
        var definition = ProfileEnvironment.GetDefinition(request.Profile, config, _paths);
        var key = _secrets.Load(request.Profile);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = request.ProjectDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in BuildArguments(request))
        {
            startInfo.ArgumentList.Add(argument);
        }
        ProfileEnvironment.Apply(startInfo, definition, key);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _activeProcess = process;
        var emittedStreamingText = false;
        try
        {
            process.Start();
            startInfo.Environment.Remove(definition.AuthenticationVariable);
            key = string.Empty;

            await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            var stdoutTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                {
                    foreach (var item in ClaudeStreamParser.Parse(line, emittedStreamingText))
                    {
                        if (item.Kind == ClaudeOutputKind.Text && item.Text.Length > 0)
                        {
                            emittedStreamingText = true;
                        }
                        onOutput(item);
                    }
                }
            }, cancellationToken);

            var stderrTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        onOutput(new ClaudeOutput(ClaudeOutputKind.Error, line + Environment.NewLine));
                    }
                }
            }, cancellationToken);

            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken));
            return new ClaudeRunResult(process.ExitCode, process.ExitCode == 0, request.SessionId);
        }
        catch (OperationCanceledException)
        {
            TryStop();
            throw;
        }
        finally
        {
            key = string.Empty;
            _activeProcess = null;
        }
    }

    public void TryStop()
    {
        try
        {
            if (_activeProcess is { HasExited: false })
            {
                _activeProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between the check and Kill.
        }
    }
}

public static class ClaudeStreamParser
{
    public static IReadOnlyList<ClaudeOutput> Parse(string line, bool streamingTextAlreadyEmitted)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = GetString(root, "type");

            if (type == "stream_event" && root.TryGetProperty("event", out var eventElement))
            {
                var eventType = GetString(eventElement, "type");
                if (eventType == "content_block_delta" &&
                    eventElement.TryGetProperty("delta", out var delta) &&
                    GetString(delta, "type") == "text_delta")
                {
                    var text = GetString(delta, "text");
                    return string.IsNullOrEmpty(text) ? [] : [new ClaudeOutput(ClaudeOutputKind.Text, text)];
                }
                if (eventType == "content_block_start" &&
                    eventElement.TryGetProperty("content_block", out var block) &&
                    GetString(block, "type") == "tool_use")
                {
                    var name = GetString(block, "name") ?? "tool";
                    return [new ClaudeOutput(ClaudeOutputKind.Tool, $"\n[工具] {name}\n")];
                }
                return [];
            }

            if (type == "assistant" && !streamingTextAlreadyEmitted &&
                root.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                var text = string.Concat(content.EnumerateArray()
                    .Where(item => GetString(item, "type") == "text")
                    .Select(item => GetString(item, "text")));
                return string.IsNullOrEmpty(text) ? [] : [new ClaudeOutput(ClaudeOutputKind.Text, text)];
            }

            if (type == "result")
            {
                var isError = root.TryGetProperty("is_error", out var errorElement) && errorElement.ValueKind == JsonValueKind.True;
                var result = GetString(root, "result");
                if (isError && !string.IsNullOrWhiteSpace(result))
                {
                    return [new ClaudeOutput(ClaudeOutputKind.Error, result + Environment.NewLine)];
                }
                if (!streamingTextAlreadyEmitted && !string.IsNullOrWhiteSpace(result))
                {
                    return [new ClaudeOutput(ClaudeOutputKind.Text, result)];
                }
                return [];
            }

            if (type == "system" && GetString(root, "subtype") == "init")
            {
                var sessionId = GetString(root, "session_id");
                return string.IsNullOrWhiteSpace(sessionId)
                    ? []
                    : [new ClaudeOutput(ClaudeOutputKind.Status, $"会话 {sessionId}\n")];
            }

            return [];
        }
        catch (JsonException)
        {
            return [new ClaudeOutput(ClaudeOutputKind.Status, line + Environment.NewLine)];
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
