using System.Text;

namespace KClaudeDesktop.Core;

public static class AtomicFile
{
    public static void WriteUtf8(string path, string content, bool createBackup = true)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Target directory is missing.");
        Directory.CreateDirectory(directory);

        if (createBackup && File.Exists(path))
        {
            var backupRoot = Path.Combine(directory, "backups");
            Directory.CreateDirectory(backupRoot);
            var backup = Path.Combine(
                backupRoot,
                $"{Path.GetFileName(path)}.{DateTime.Now:yyyyMMdd-HHmmssfff}.bak");
            File.Copy(path, backup, overwrite: false);
        }

        var temp = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            if (File.Exists(path))
            {
                File.Move(temp, path, overwrite: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static string Backup(string path, string backupRoot)
    {
        Directory.CreateDirectory(backupRoot);
        var backup = Path.Combine(
            backupRoot,
            $"{Path.GetFileName(path)}.{DateTime.Now:yyyyMMdd-HHmmssfff}.bak");
        File.Copy(path, backup, overwrite: false);
        return backup;
    }
}
