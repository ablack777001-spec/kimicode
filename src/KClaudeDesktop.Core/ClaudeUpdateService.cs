using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace KClaudeDesktop.Core;

public sealed class ClaudeUpdateService
{
    public const string OfficialLatestVersionUrl = "https://downloads.claude.ai/claude-code-releases/latest";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly ClaudeCliLocator _locator;

    public ClaudeUpdateService(AppPaths paths, ClaudeCliLocator locator)
    {
        _paths = paths;
        _locator = locator;
    }

    public IReadOnlyList<ClaudeCliBackup> ListBackups()
    {
        _paths.EnsureDirectories();
        var backups = new List<ClaudeCliBackup>();
        foreach (var metadataPath in Directory.EnumerateFiles(_paths.CliBackupsRoot, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<ClaudeCliBackup>(File.ReadAllText(metadataPath), JsonOptions);
                if (item is not null && File.Exists(item.ExecutablePath))
                {
                    backups.Add(item);
                }
            }
            catch
            {
                // A corrupt metadata file is ignored and surfaced by diagnostics through a missing backup list entry.
            }
        }
        return backups.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<ClaudeUpdateCheck> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var executable = _locator.Find() ?? throw new FileNotFoundException("未找到 Claude Code CLI。");
        var currentText = await ReadVersionAsync(executable, cancellationToken);
        var currentVersion = ExtractVersion(currentText);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KClaudeDesktop/1.0");
        var latestText = (await client.GetStringAsync(OfficialLatestVersionUrl, cancellationToken)).Trim();
        var latestVersion = ExtractVersion(latestText);
        var manifestUrl = $"https://downloads.claude.ai/claude-code-releases/{latestVersion}/manifest.json";
        var manifestText = await client.GetStringAsync(manifestUrl, cancellationToken);
        using var manifestDocument = JsonDocument.Parse(manifestText);
        var manifestRoot = manifestDocument.RootElement;
        var commit = manifestRoot.TryGetProperty("commit", out var commitElement)
            ? commitElement.GetString() ?? ""
            : "";
        DateTimeOffset? buildDate = null;
        if (manifestRoot.TryGetProperty("buildDate", out var buildDateElement) &&
            DateTimeOffset.TryParse(buildDateElement.GetString(), out var parsedBuildDate))
        {
            buildDate = parsedBuildDate;
        }

        var releaseNotes = "";
        var releaseNotesUrl = "";
        try
        {
            var releaseJson = await client.GetStringAsync(
                $"https://api.github.com/repos/anthropics/claude-code/releases/tags/v{latestVersion}",
                cancellationToken);
            using var releaseDocument = JsonDocument.Parse(releaseJson);
            var releaseRoot = releaseDocument.RootElement;
            releaseNotes = releaseRoot.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString() ?? ""
                : "";
            releaseNotesUrl = releaseRoot.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString() ?? ""
                : "";
        }
        catch (HttpRequestException)
        {
            // Some versions do not have a separate GitHub Release document.
        }

        var available = latestVersion > currentVersion;
        var now = DateTimeOffset.Now;
        var result = new ClaudeUpdateCheck(
            currentVersion.ToString(),
            latestVersion.ToString(),
            available,
            now,
            available ? $"发现 Claude Code {latestVersion}" : $"当前已是最新版本 {currentVersion}",
            commit,
            buildDate,
            releaseNotes,
            releaseNotesUrl);
        AtomicFile.WriteUtf8(
            _paths.UpdateInfoPath,
            JsonSerializer.Serialize(result, JsonOptions) + Environment.NewLine,
            createBackup: false);
        return result;
    }

    public async Task<ClaudeUpdateResult> UpdateAsync(
        Action<string> onOutput,
        CancellationToken cancellationToken = default)
    {
        EnsureNoRunningClaude();
        var executable = _locator.Find() ?? throw new FileNotFoundException("未找到 Claude Code CLI。");
        var previousVersion = await ReadVersionAsync(executable, cancellationToken);
        var backup = await BackupCurrentAsync(executable, previousVersion, cancellationToken);
        onOutput($"已备份 {previousVersion}：{backup.ExecutablePath}");

        using var process = CreateProcess(executable, "update");
        process.Start();
        var stdoutTask = PumpAsync(process.StandardOutput, onOutput, cancellationToken);
        var stderrTask = PumpAsync(process.StandardError, onOutput, cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken));

        if (process.ExitCode != 0)
        {
            return new ClaudeUpdateResult(
                false,
                previousVersion,
                previousVersion,
                backup,
                $"升级命令退出码 {process.ExitCode}；原版本备份已保留，可手动回滚。");
        }

        var currentVersion = await ReadVersionAsync(executable, cancellationToken);
        var capabilities = await _locator.CheckGuiCapabilitiesAsync(cancellationToken);
        if (!capabilities.Success)
        {
            return new ClaudeUpdateResult(
                false,
                previousVersion,
                currentVersion,
                backup,
                "CLI 已升级，但 GUI 能力检查失败：" + capabilities.Detail);
        }

        return new ClaudeUpdateResult(
            true,
            previousVersion,
            currentVersion,
            backup,
            $"升级完成：{previousVersion} → {currentVersion}");
    }

    public async Task<string> RollbackAsync(
        ClaudeCliBackup selectedBackup,
        Action<string> onOutput,
        CancellationToken cancellationToken = default)
    {
        EnsureNoRunningClaude();
        var backupPath = Path.GetFullPath(selectedBackup.ExecutablePath);
        var root = Path.GetFullPath(_paths.CliBackupsRoot) + Path.DirectorySeparatorChar;
        if (!backupPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(backupPath))
        {
            throw new InvalidOperationException("回滚文件不在受管备份目录内或已丢失。");
        }

        var hash = await ComputeSha256Async(backupPath, cancellationToken);
        if (!hash.Equals(selectedBackup.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("回滚文件 SHA-256 与元数据不一致，已停止恢复。");
        }

        var backupVersion = await ReadVersionAsync(backupPath, cancellationToken);
        var currentExecutable = _locator.Find() ?? throw new FileNotFoundException("未找到当前 Claude Code CLI。");
        var currentVersion = await ReadVersionAsync(currentExecutable, cancellationToken);
        var safetyBackup = await BackupCurrentAsync(currentExecutable, currentVersion, cancellationToken);
        onOutput($"恢复前已备份当前版本：{safetyBackup.ExecutablePath}");

        var targetDirectory = Path.GetDirectoryName(currentExecutable)!;
        var temp = Path.Combine(targetDirectory, $"claude.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(backupPath, temp, overwrite: true);
            File.Move(temp, currentExecutable, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        var restoredVersion = await ReadVersionAsync(currentExecutable, cancellationToken);
        var capabilities = await _locator.CheckGuiCapabilitiesAsync(cancellationToken);
        if (!capabilities.Success)
        {
            throw new InvalidOperationException("已恢复 CLI，但 GUI 能力检查失败：" + capabilities.Detail);
        }
        return $"已回滚：{currentVersion} → {restoredVersion}（备份标记 {backupVersion}）";
    }

    private async Task<ClaudeCliBackup> BackupCurrentAsync(
        string executable,
        string version,
        CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        var stamp = DateTimeOffset.Now;
        var safeVersion = string.Concat(version.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_'));
        var backupPath = Path.Combine(
            _paths.CliBackupsRoot,
            $"claude-{safeVersion}-{stamp:yyyyMMdd-HHmmssfff}.exe");
        File.Copy(executable, backupPath, overwrite: false);
        var hash = await ComputeSha256Async(backupPath, cancellationToken);
        var metadataPath = backupPath + ".json";
        var item = new ClaudeCliBackup(backupPath, metadataPath, version, hash, stamp);
        AtomicFile.WriteUtf8(metadataPath, JsonSerializer.Serialize(item, JsonOptions) + Environment.NewLine, createBackup: false);
        return item;
    }

    private static async Task<string> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        using var process = CreateProcess(executable, "--version");
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await stdoutTask).Trim();
        var error = (await stderrTask).Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "claude --version 失败" : error);
        }
        return output;
    }

    private static Version ExtractVersion(string text)
    {
        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                    ?? throw new InvalidDataException("版本文本为空。");
        if (!Version.TryParse(token, out var version))
        {
            throw new InvalidDataException("无法解析 Claude Code 版本：" + token);
        }
        return version;
    }

    private static Process CreateProcess(string executable, string argument)
    {
        var process = new Process
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
        process.StartInfo.ArgumentList.Add(argument);
        return process;
    }

    private static async Task PumpAsync(
        StreamReader reader,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                onOutput(line);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void EnsureNoRunningClaude()
    {
        var running = Process.GetProcessesByName("claude");
        try
        {
            if (running.Any(process => !process.HasExited))
            {
                throw new InvalidOperationException("请先退出所有 Claude Code 会话，再执行升级或回滚。");
            }
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }
    }
}
