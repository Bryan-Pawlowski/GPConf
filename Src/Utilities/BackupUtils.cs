namespace GPConf.Utilities;

public static class BackupUtils
{
    // Rotates up to maxBackups numbered backups of `path` (bak1 = newest) before an overwrite,
    // so a bad write can be recovered from. No-op if `path` doesn't exist yet (nothing to back up).
    public static void RotateBackups(string path, int maxBackups = 5)
    {
        if (!File.Exists(path)) return;
        for (int i = maxBackups; i >= 2; i--)
        {
            var older = $"{path}.bak{i - 1}";
            if (File.Exists(older)) File.Copy(older, $"{path}.bak{i}", overwrite: true);
        }
        File.Copy(path, $"{path}.bak1", overwrite: true);
    }
}
