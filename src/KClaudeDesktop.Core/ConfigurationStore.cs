using System.Security;
using System.Text.Json;

namespace KClaudeDesktop.Core;

public sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly AppPaths _paths;

    public ConfigurationStore(AppPaths paths)
    {
        _paths = paths;
    }

    public SwitcherConfig LoadSwitcherConfig()
    {
        _paths.EnsureDirectories();
        if (!File.Exists(_paths.ConfigPath))
        {
            var created = new SwitcherConfig();
            SaveSwitcherConfig(created);
            return created;
        }

        var config = JsonSerializer.Deserialize<SwitcherConfig>(File.ReadAllText(_paths.ConfigPath), JsonOptions)
                     ?? throw new InvalidDataException("config.json is empty.");
        Validate(config);
        return config;
    }

    public void SaveSwitcherConfig(SwitcherConfig config)
    {
        Validate(config);
        _paths.EnsureDirectories();
        if (File.Exists(_paths.ConfigPath))
        {
            AtomicFile.Backup(_paths.ConfigPath, _paths.BackupsRoot);
        }
        AtomicFile.WriteUtf8(
            _paths.ConfigPath,
            JsonSerializer.Serialize(config, JsonOptions) + Environment.NewLine,
            createBackup: false);
    }

    public UiSettings LoadUiSettings()
    {
        _paths.EnsureDirectories();
        if (!File.Exists(_paths.UiSettingsPath))
        {
            return new UiSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(_paths.UiSettingsPath), JsonOptions)
                   ?? new UiSettings();
        }
        catch (JsonException)
        {
            return new UiSettings();
        }
    }

    public void SaveUiSettings(UiSettings settings)
    {
        _paths.EnsureDirectories();
        AtomicFile.WriteUtf8(
            _paths.UiSettingsPath,
            JsonSerializer.Serialize(settings, JsonOptions) + Environment.NewLine,
            createBackup: false);
    }

    private static void Validate(SwitcherConfig config)
    {
        if (!string.Equals(config.DefaultProfile, "member", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The default profile must remain member to prevent surprise API charges.");
        }
        if (config.MemberContext is not ("1m" or "256k"))
        {
            throw new InvalidDataException("memberContext must be 1m or 256k.");
        }
        config.SchemaVersion = 1;
        config.DefaultProfile = "member";
    }
}

public sealed class SecretStore
{
    private readonly AppPaths _paths;

    public SecretStore(AppPaths paths)
    {
        _paths = paths;
    }

    public SecretStatus GetStatus(ClaudeProfile profile)
    {
        var path = GetPath(profile);
        if (!File.Exists(path))
        {
            return new SecretStatus(false, false, 0, "尚未录入");
        }

        try
        {
            var value = DpapiService.Unprotect(File.ReadAllText(path));
            var length = value.Length;
            value = string.Empty;
            return new SecretStatus(true, true, length, $"已加密保存，可解密，长度 {length}");
        }
        catch
        {
            return new SecretStatus(true, false, 0, "密文存在，但当前 Windows 用户无法解密");
        }
    }

    public string Load(ClaudeProfile profile)
    {
        var path = GetPath(profile);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{profile} Key 尚未录入，请先打开设置。 ");
        }
        var value = DpapiService.Unprotect(File.ReadAllText(path));
        if (value.Length is < 20 or > 512)
        {
            throw new InvalidDataException("Key 长度明显异常，请在设置中重新录入。");
        }
        return value;
    }

    public void Save(ClaudeProfile profile, SecureString secureValue)
    {
        ArgumentNullException.ThrowIfNull(secureValue);
        if (secureValue.Length is < 20 or > 512)
        {
            throw new ArgumentException("Key 长度应在 20 到 512 个字符之间。", nameof(secureValue));
        }

        _paths.EnsureDirectories();
        var path = GetPath(profile);
        if (File.Exists(path))
        {
            AtomicFile.Backup(path, _paths.BackupsRoot);
        }
        AtomicFile.WriteUtf8(path, DpapiService.Protect(secureValue) + Environment.NewLine, createBackup: false);
        TightenAcl(path, directory: false);
        TightenAcl(_paths.SecretsRoot, directory: true);
    }

    private string GetPath(ClaudeProfile profile) =>
        profile == ClaudeProfile.Member ? _paths.MemberSecretPath : _paths.PlatformSecretPath;

    private static void TightenAcl(string path, bool directory)
    {
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var grant = directory ? $"{identity}:(OI)(CI)(F)" : $"{identity}:(F)";
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(path);
        process.StartInfo.ArgumentList.Add("/inheritance:r");
        process.StartInfo.ArgumentList.Add("/grant:r");
        process.StartInfo.ArgumentList.Add(grant);
        process.Start();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Key 已写入，但 Windows ACL 收紧失败。请运行诊断后再使用。 ");
        }
    }
}
