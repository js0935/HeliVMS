using System.IO;
using System.IO.Compression;

namespace HeliVMS.Services;

public sealed class BackupService {
    private readonly string _dataDir;
    private readonly string _backupDir;

    public BackupService() {
        _dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        _backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
    }

    public string CreateBackup() {
        if (!Directory.Exists(_dataDir)) throw new DirectoryNotFoundException($"Data directory not found: {_dataDir}");
        if (!Directory.Exists(_backupDir)) Directory.CreateDirectory(_backupDir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var zipPath = Path.Combine(_backupDir, $"HeliVMS_backup_{timestamp}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.GetFiles(_dataDir, "*.json")) {
            var entryName = Path.GetFileName(file);
            zip.CreateEntryFromFile(file, entryName);
        }

        return zipPath;
    }

    public string[] ListBackups() {
        if (!Directory.Exists(_backupDir)) return [];
        return Directory.GetFiles(_backupDir, "HeliVMS_backup_*.zip")
            .OrderByDescending(f => f)
            .ToArray();
    }

    public void RestoreBackup(string zipPath) {
        if (!File.Exists(zipPath)) throw new FileNotFoundException($"Backup file not found: {zipPath}");
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries) {
            var destPath = Path.Combine(_dataDir, entry.FullName);
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    public void CleanupOldBackups(int maxBackups = 10) {
        var backups = ListBackups();
        if (backups.Length <= maxBackups) return;
        foreach (var old in backups.Skip(maxBackups)) {
            try { File.Delete(old); } catch { }
        }
    }
}
