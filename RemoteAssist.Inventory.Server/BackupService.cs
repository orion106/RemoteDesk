using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RemoteAssist.Inventory;

namespace RemoteAssist.Inventory.Server;

public sealed partial class InventoryStore
{
    public string CreateBackup(string backupDirectory)
    {
        var backupRoot = Path.GetFullPath(backupDirectory);
        Require(!IsInside(backupRoot, DataDirectory), "Резервная копия должна находиться вне dataset с рабочей базой.");
        Directory.CreateDirectory(backupRoot);
        var name = $"inventory-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var staging = Path.Combine(backupRoot, ".pending-" + name); var final = Path.Combine(backupRoot, name);
        lock (gate)
        {
            Directory.CreateDirectory(staging);
            using (var source = Open()) using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(staging, "inventory.db"), Pooling = false }.ToString()))
            {
                destination.Open(); source.BackupDatabase(destination);
                // A backup must open from a read-only mount without creating WAL/SHM sidecars.
                using var standalone = destination.CreateCommand(); standalone.CommandText = "PRAGMA journal_mode=DELETE"; standalone.ExecuteScalar();
            }
            CopyTree(PhotoDirectory, Path.Combine(staging, "photos")); CopyTree(Path.Combine(DataDirectory, "config"), Path.Combine(staging, "config"));
            var hashes = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(staging, p).Replace('\\', '/'), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new BackupManifest { InstanceId = Snapshot().InstanceId, CreatedUtc = DateTimeOffset.UtcNow, Files = hashes }, Json));
            VerifyBackup(staging); Directory.Move(staging, final);
            foreach (var expired in Directory.EnumerateDirectories(backupRoot, "inventory-*").Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0 && File.Exists(Path.Combine(p, "manifest.json"))).OrderByDescending(p => JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(p, "manifest.json")), Json)!.CreatedUtc).Skip(14))
            {
                if (!IsInside(expired, backupRoot) || Path.GetFullPath(expired) == backupRoot) throw new IOException("Unsafe retention path.");
                Directory.Delete(expired, true);
            }
        }
        return final;
    }
    public static void VerifyBackup(string directory)
    {
        var root = Path.GetFullPath(directory);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(root, "manifest.json")), Json) ?? throw new InvalidDataException("Нет манифеста копии.");
        if (!manifest.Files.ContainsKey("inventory.db")) throw new InvalidDataException("Нет базы в манифесте.");
        foreach (var (relative, hash) in manifest.Files)
        {
            var file = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsInside(file, root) || !File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Копия повреждена: {relative}");
        }
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "inventory.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA integrity_check";
        if (!string.Equals((string?)cmd.ExecuteScalar(), "ok", StringComparison.Ordinal)) throw new InvalidDataException("Ошибка целостности SQLite.");
        var state = Read(db);
        foreach (var photo in state.Photos) if (!manifest.Files.ContainsKey("photos/" + photo.Id)) throw new InvalidDataException($"Нет фотографии {photo.Id}.");
    }
    public static void RestoreBackup(string backup, string destination)
    {
        VerifyBackup(backup); var source = Path.GetFullPath(backup); var target = Path.GetFullPath(destination);
        if (IsInside(target, source) || IsInside(source, target)) throw new IOException("Источник и каталог восстановления не должны пересекаться.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("Восстановление разрешено только в новый пустой каталог. Остановите службу и сохраните старый dataset.");
        Directory.CreateDirectory(target); CopyTree(source, target);
        // Invalidate all outstanding bearer sessions after restoration.
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(target, "inventory.db"), Pooling = false }.ToString()); db.Open();
        var state = Read(db); state.RetrievedUtc = DateTimeOffset.UtcNow;
        using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM sessions; UPDATE state SET json=$json WHERE id=1"; cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, Json)); cmd.ExecuteNonQuery();
    }
    private static bool IsInside(string candidate, string root) => string.Equals(Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Символические ссылки в хранилище не поддерживаются."); File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false); }
        foreach (var dir in Directory.EnumerateDirectories(source)) { if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("Символические ссылки в хранилище не поддерживаются."); CopyTree(dir, Path.Combine(target, Path.GetFileName(dir))); }
    }
    private sealed class BackupManifest { public string InstanceId { get; set; } = ""; public DateTimeOffset CreatedUtc { get; set; } public Dictionary<string, string> Files { get; set; } = []; }
}

public sealed class BackupStatus
{
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public string? LastError { get; set; }
    public bool BackupDirectoryConfigured { get; set; }
}

public sealed class BackupService(InventoryStore store, IConfiguration configuration, ILogger<BackupService> logger) : BackgroundService
{
    private readonly SemaphoreSlim backupGate = new(1, 1);
    private readonly BackupStatus status = new();
    private string? DirectoryPath => configuration["INVENTORY_BACKUP_DIR"];
    public BackupStatus Status { get { lock (status) return new BackupStatus { LastSuccessUtc = status.LastSuccessUtc, LastError = status.LastError, BackupDirectoryConfigured = !string.IsNullOrWhiteSpace(DirectoryPath) }; } }
    public async Task<string> Run(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath)) throw new StoreException(400, "backup_unconfigured", "Не задан INVENTORY_BACKUP_DIR.");
        await backupGate.WaitAsync(cancellationToken);
        try { var result = await Task.Run(() => store.CreateBackup(DirectoryPath!), cancellationToken); lock (status) { status.LastSuccessUtc = DateTimeOffset.UtcNow; status.LastError = null; } return result; }
        catch (Exception ex) { lock (status) status.LastError = "Не удалось создать или проверить резервную копию. Проверьте место, права и журнал службы."; logger.LogError(ex, "Inventory backup failed"); throw; }
        finally { backupGate.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(DirectoryPath)) try { await Run(stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; } catch { /* Status and structured log retain the failure. Retry daily. */ }
            try { await Task.Delay(TimeSpan.FromDays(1), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
