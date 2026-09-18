using Microsoft.Data.Sqlite;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace RemoteAssist;

public sealed class LabStore
{
    private readonly string _connection;
    private readonly object _sync = new();
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAssist", "lab.db");
    public LabStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 10 }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS records(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(kind,id)); PRAGMA user_version=1;";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connection); db.Open(); return db; }
    public List<T> Load<T>(string kind)
    {
        lock (_sync)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM records WHERE kind=$kind ORDER BY rowid"; cmd.Parameters.AddWithValue("$kind", kind);
            using var reader = cmd.ExecuteReader(); var result = new List<T>();
            while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0)) ?? throw new InvalidDataException("Повреждена запись базы: " + kind));
            return result;
        }
    }
    public void Save<T>(string kind, string id, T value)
    {
        lock (_sync)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO records(kind,id,payload) VALUES($kind,$id,$payload) ON CONFLICT(kind,id) DO UPDATE SET payload=excluded.payload";
            cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value)); cmd.ExecuteNonQuery();
        }
    }
    public void ReplaceComputers(IEnumerable<LabComputer> computers)
        => ReplaceComputerState(computers);

    public void ReplaceComputerState(IEnumerable<LabComputer> computers, IEnumerable<LabSchedule>? schedules = null, IEnumerable<InventoryComputerLink>? links = null)
    {
        lock (_sync)
        {
            using var db = Open(); using var transaction = db.BeginTransaction(); using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            void Replace<T>(string kind, IEnumerable<T> values, Func<T, string> id)
            {
                cmd.Parameters.Clear(); cmd.CommandText = "DELETE FROM records WHERE kind=$kind";
                cmd.Parameters.AddWithValue("$kind", kind); cmd.ExecuteNonQuery();
                foreach (var value in values)
                {
                    cmd.CommandText = "INSERT INTO records(kind,id,payload) VALUES($kind,$id,$payload)";
                    cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id(value));
                    cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value)); cmd.ExecuteNonQuery();
                }
            }
            Replace("computer", computers, x => x.Id);
            if (schedules is not null) Replace("schedule", schedules, x => x.Id);
            if (links is not null) Replace(InventoryLinkService.RecordKind, links, x => x.LocalComputerId);
            transaction.Commit();
        }
    }
    public void Delete(string kind, string id)
    {
        lock (_sync)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM records WHERE kind=$kind AND id=$id";
            cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
        }
    }
}

public sealed class LabSecrets(ICredentialStore store)
{
    public static string Key(string service, string identity) => "RemoteAssist/Lab/" + service + "/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Trim())));
    public string Read(string service, string identity)
    {
        using var secret = store.Read(Key(service, identity)); return secret is null ? "" : new NetworkCredential("", secret).Password;
    }
    public void Save(string service, string identity, SecureString secret)
    {
        if (secret.Length > 0) store.Write(Key(service, identity), service, secret);
    }
    public void Delete(string service, string identity) => store.Delete(Key(service, identity));
}

public static class MachineIdentity
{
    public static string Normalize(string? value) => (value ?? "").Trim().Trim('{', '}').ToUpperInvariant();
    public static bool Valid(string? value)
    {
        var v = Normalize(value); var compact = new string(v.Where(char.IsLetterOrDigit).ToArray());
        return compact.Length >= 4 && compact.Distinct().Count() > 1 && !new[] { "UNKNOWN", "NONE", "DEFAULT", "TOBEFILLED", "SYSTEMSERIAL", "NOTSPECIFIED", "NOTAPPLICABLE" }.Any(x => compact.Contains(x, StringComparison.Ordinal));
    }
    public static bool Matches(LabComputer computer, MachineSnapshot snapshot)
    {
        if (!snapshot.Reachable || snapshot.Os == LabOs.Unknown) return false;
        var uuid = Valid(computer.Uuid) && Valid(snapshot.Uuid);
        var serial = Valid(computer.Serial) && Valid(snapshot.Serial);
        if (uuid && Normalize(computer.Uuid) != Normalize(snapshot.Uuid) || serial && Normalize(computer.Serial) != Normalize(snapshot.Serial)) return false;
        return uuid || serial;
    }
    public static bool SameHardware(LabComputer left, LabComputer right)
    {
        var uuid = Valid(left.Uuid) && Valid(right.Uuid); var serial = Valid(left.Serial) && Valid(right.Serial);
        if (uuid && Normalize(left.Uuid) != Normalize(right.Uuid) || serial && Normalize(left.Serial) != Normalize(right.Serial)) return false;
        return uuid && Normalize(left.Uuid) == Normalize(right.Uuid) || serial && Normalize(left.Serial) == Normalize(right.Serial);
    }
    public static List<LabComputer> MergeInventory(IEnumerable<LabComputer> existing, IEnumerable<LabComputer> incoming)
    {
        var result = existing.Select(x => x.Copy()).ToList();
        foreach (var source in incoming)
        {
            var byId = result.Where(x => x.GlpiIds.Intersect(source.GlpiIds).Any()).ToArray();
            var byHardware = result.Where(x => SameHardware(x, source)).ToArray();
            var candidates = byId.Length == 1 ? byId : byHardware;
            if (candidates.Length != 1)
            {
                source.InventoryNote = candidates.Length > 1 ? "Неоднозначный идентификатор: требуется ручное сопоставление" : !Valid(source.Uuid) && !Valid(source.Serial) ? "Нет надёжного аппаратного ID: выполните диагностику" : "";
                result.Add(source.Copy()); continue;
            }
            var target = candidates[0];
            if (byId.Length == 1 && (Valid(target.Uuid) && Valid(source.Uuid) && Normalize(target.Uuid) != Normalize(source.Uuid) || Valid(target.Serial) && Valid(source.Serial) && Normalize(target.Serial) != Normalize(source.Serial)))
            {
                target.InventoryNote = "Аппаратный ID GLPI изменился; требуется проверка. Прежняя идентичность сохранена."; continue;
            }
            MergeInto(target, source);
        }
        return result;
    }
    public static void MergeInto(LabComputer target, LabComputer source)
    {
        if (source.Name.Length > 0) target.Name = source.Name;
        if (!target.LocalPlacement && source.Room.Length > 0) target.Room = source.Room;
        if (!Valid(target.Uuid)) target.Uuid = source.Uuid;
        if (!Valid(target.Serial)) target.Serial = source.Serial;
        target.Manufacturer = source.Manufacturer; target.Model = source.Model;
        target.GlpiIds = target.GlpiIds.Union(source.GlpiIds).ToList(); target.MacAddresses = target.MacAddresses.Union(source.MacAddresses, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var endpoint in source.Systems)
        {
            var found = target.Systems.FirstOrDefault(x => x.Os == endpoint.Os && x.Address.Equals(endpoint.Address, StringComparison.OrdinalIgnoreCase));
            if (found is null) target.Systems.Add(endpoint);
            else { found.Description = endpoint.Description; found.InventoryAt = endpoint.InventoryAt; }
        }
    }
}

public static class ProfilePolicy
{
    public static bool Matches(string login, string rule) => Regex.IsMatch(login, "^" + Regex.Escape(rule).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static string Reason(StudentProfile p, LabOptions options)
    {
        var shortName = p.Login.Split('\\').Last().Split('@')[0];
        if (p.Special || p.Sid.EndsWith("-500", StringComparison.Ordinal) || p.Sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") return "Системный профиль / встроенный администратор";
        if (new[] { "admin", "administrator", "администратор" }.Contains(shortName, StringComparer.OrdinalIgnoreCase) || LabOptions.Lines(options.ProtectedUsers).Any(x => Matches(shortName, x) || Matches(p.Login, x))) return "Защищённая учётная запись";
        if (p.Loaded) return "Профиль используется";
        if (p.Administrator != false) return p.Administrator == true ? "Администратор" : "Права владельца не подтверждены";
        if (!p.LocalPathSafe) return "Небезопасный, перенаправленный или неизвестный путь";
        if (string.IsNullOrWhiteSpace(p.Login) || !LabOptions.Lines(options.StudentMasks).Any(x => Matches(p.Login, x) || Matches(shortName, x))) return "Не соответствует списку студентов";
        return "";
    }
}

public static class MachineMutationGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Unconfirmed = new(StringComparer.OrdinalIgnoreCase);
    private static IEnumerable<string> Names(LabComputer pc) => pc.Systems.Select(x => x.Address).Append(pc.Name).Append("ID:" + pc.Id).Append("UUID:" + pc.Uuid);
    private static string[] Keys(IEnumerable<string> names) => names.Where(x => !string.IsNullOrWhiteSpace(x) && x != "UUID:").SelectMany(x => x.StartsWith("ID:") || x.StartsWith("UUID:") || IPAddress.TryParse(x, out _) ? new[] { x } : new[] { x, x.Split('.')[0] }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    public static void SetUnconfirmed(LabComputer pc, string jobId, bool blocked)
    {
        foreach (var key in Keys(Names(pc))) { var set = Unconfirmed.GetOrAdd(key, _ => new()); if (blocked) set[jobId] = 0; else set.TryRemove(jobId, out _); }
    }
    public static Task<IDisposable> AcquireHostAsync(string host, CancellationToken token) => AcquireAsync([host], token, false);
    public static Task<IDisposable> AcquireComputerAsync(LabComputer pc, CancellationToken token, bool reconciliation = false) => AcquireAsync(Names(pc), token, reconciliation);
    private static async Task<IDisposable> AcquireAsync(IEnumerable<string> names, CancellationToken token, bool reconciliation)
    {
        var keys = Keys(names);
        var held = new List<SemaphoreSlim>();
        try { foreach (var key in keys) { var gate = Gates.GetOrAdd(key, _ => new(1, 1)); await gate.WaitAsync(token); held.Add(gate); } if (!reconciliation && keys.Any(x => Unconfirmed.TryGetValue(x, out var jobs) && !jobs.IsEmpty)) throw new InvalidOperationException("На ПК есть неподтверждённое задание. Сначала выполните сверку в разделе «Задания»."); return new Lease(held); }
        catch { foreach (var gate in held) gate.Release(); throw; }
    }
    private sealed class Lease(List<SemaphoreSlim> gates) : IDisposable { private int _disposed; public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) foreach (var gate in gates.AsEnumerable().Reverse()) gate.Release(); } }
}
