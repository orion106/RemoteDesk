using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RemoteAssist.Inventory;

namespace RemoteAssist.Inventory.Server;

public sealed class StoreException(int status, string code, string message, object? current = null) : Exception(message)
{
    public int Status { get; } = status;
    public ApiError Error { get; } = new() { Code = code, Message = message, Current = current };
}

public sealed partial class InventoryStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "inventory.db");
    public string PhotoDirectory => Path.Combine(DataDirectory, "photos");
    public InventoryStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(PhotoDirectory);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "config"));
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS state(id INTEGER PRIMARY KEY CHECK(id=1),json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS commands(id TEXT PRIMARY KEY,actor TEXT NOT NULL,fingerprint TEXT NOT NULL,result TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY,username TEXT NOT NULL UNIQUE COLLATE NOCASE,json TEXT NOT NULL,salt TEXT NOT NULL,hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions(hash TEXT PRIMARY KEY,user_id TEXT NOT NULL,expires TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT OR IGNORE INTO state(id,json) VALUES(1,$json)";
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new InventorySnapshot { InstanceId = Guid.NewGuid().ToString("N") }, Json));
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA busy_timeout=15000; PRAGMA foreign_keys=ON;"; cmd.ExecuteNonQuery();
        return db;
    }
    private static T Copy<T>(T item) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(item, Json), Json)!;
    private static InventorySnapshot Read(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT json FROM state WHERE id=1";
        return JsonSerializer.Deserialize<InventorySnapshot>((string)cmd.ExecuteScalar()!, Json)!;
    }
    public InventorySnapshot Snapshot()
    {
        lock (gate) { using var db = Open(); var state = Read(db); state.RetrievedUtc = DateTimeOffset.UtcNow; return state; }
    }
    private MutationResult Mutate(Command command, UserDto actor, Action<InventorySnapshot> change, string? fingerprint = null)
    {
        RequireGuid(command.CommandId);
        fingerprint ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, command.GetType(), Json))));
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            using var prior = db.CreateCommand(); prior.Transaction = tx; prior.CommandText = "SELECT actor,fingerprint,result FROM commands WHERE id=$id"; prior.Parameters.AddWithValue("$id", command.CommandId);
            using (var reader = prior.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != actor.Id || reader.GetString(1) != fingerprint) throw new StoreException(409, "command_reused", "Идентификатор операции уже использован для другого запроса.");
                    var result = JsonSerializer.Deserialize<MutationResult>(reader.GetString(2), Json)!; result.AlreadyApplied = true; return result;
                }
            }
            var state = Read(db, tx); change(state); state.Revision++;
            var response = new MutationResult { Revision = state.Revision };
            using var save = db.CreateCommand(); save.Transaction = tx; save.CommandText = "UPDATE state SET json=$json WHERE id=1; INSERT INTO commands(id,actor,fingerprint,result) VALUES($id,$actor,$fingerprint,$result)";
            save.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, Json)); save.Parameters.AddWithValue("$id", command.CommandId); save.Parameters.AddWithValue("$actor", actor.Id); save.Parameters.AddWithValue("$fingerprint", fingerprint); save.Parameters.AddWithValue("$result", JsonSerializer.Serialize(response, Json)); save.ExecuteNonQuery(); tx.Commit(); return response;
        }
    }
    private static void RequireGuid(string id) { if (!Guid.TryParseExact(id, "N", out _) && !Guid.TryParseExact(id, "D", out _)) throw new StoreException(400, "invalid_id", "Требуется идентификатор GUID."); }
    private static void Require(bool condition, string message) { if (!condition) throw new StoreException(400, "invalid", message); }
    private static T Existing<T>(List<T> list, string id) where T : VersionedEntity => list.Find(x => x.Id == id) ?? throw new StoreException(404, "not_found", "Запись не найдена.");
    private static void Version(VersionedEntity? existing, long version)
    {
        if ((existing?.Version ?? 0) != version) throw new StoreException(409, "conflict", "Запись изменена другим администратором. Обновите данные; ваш черновик можно сохранить отдельно.", existing);
    }
    private static void History(InventorySnapshot s, UserDto user, string type, string id, string action, object? before, object? after) => s.History.Add(new HistoryDto { EntityType = type, EntityId = id, Action = action, Author = user.DisplayName.Length > 0 ? user.DisplayName : user.Username, DateUtc = DateTimeOffset.UtcNow, BeforeJson = before is null ? "" : JsonSerializer.Serialize(before, Json), AfterJson = after is null ? "" : JsonSerializer.Serialize(after, Json) });
    private static void Save<T>(List<T> list, T value) where T : VersionedEntity { var idx = list.FindIndex(x => x.Id == value.Id); if (idx < 0) list.Add(value); else list[idx] = value; }
    private static void ValidateRoom(InventorySnapshot s, RoomDto r)
    {
        RequireGuid(r.Id); Require(!string.IsNullOrWhiteSpace(r.Building) && !string.IsNullOrWhiteSpace(r.Number), "Укажите корпус и кабинет.");
        r.Building = r.Building.Trim().ToUpperInvariant(); r.Number = r.Number.Trim();
        Require(r.Floor >= -5 && r.Floor <= 200, "Некорректный этаж.");
        Require(r.Kind is "room" or "corridor", "Неизвестный тип помещения.");
        Require(!s.Rooms.Any(x => x.Id != r.Id && string.Equals(x.Building.Trim(), r.Building.Trim(), StringComparison.OrdinalIgnoreCase) && string.Equals(x.Number.Trim(), r.Number.Trim(), StringComparison.OrdinalIgnoreCase)), "Такой кабинет уже существует.");
    }
    private static void ValidateAsset(InventorySnapshot s, AssetDto a)
    {
        RequireGuid(a.Id); Require(!string.IsNullOrWhiteSpace(a.Type), "Укажите тип оборудования.");
        if (a.RoomId is not null) Existing(s.Rooms, a.RoomId);
        Require(a.PhoneNumber.Length <= 100, "Номер телефона слишком длинный.");
        if (a.ParentAssetId is not null)
        {
            var visited = new HashSet<string> { a.Id };
            var parentId = a.ParentAssetId;
            while (parentId is not null)
            {
                Require(visited.Add(parentId), "Нельзя связывать технику по кругу или с самой собой.");
                var parent = Existing(s.Assets, parentId);
                parentId = parent.ParentAssetId;
            }
        }
        Require(a.X.HasValue == a.Y.HasValue && (!a.X.HasValue || (double.IsFinite(a.X.Value) && double.IsFinite(a.Y!.Value) && a.X >= 0 && a.X <= 1 && a.Y >= 0 && a.Y <= 1)), "Координаты должны быть в пределах 0–1.");
        Require(a.RoomId is not null || a.X is null, "Для расстановки выберите кабинет.");
        if (a.Archived) Require(!s.Cartridges.Any(c => c.PrinterId == a.Id), "Снимите картриджи перед архивированием принтера.");
        if (s.Cartridges.Any(c => c.PrinterId == a.Id)) Require(IsPrinter(a), "Снимите картриджи перед изменением типа принтера.");
    }
    private static bool IsPrinter(AssetDto asset) => asset.Type.Contains("принтер", StringComparison.OrdinalIgnoreCase) || asset.Type.Contains("мфу", StringComparison.OrdinalIgnoreCase) || asset.Type.Contains("printer", StringComparison.OrdinalIgnoreCase);
    private static string Author(UserDto actor) => string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.Username : actor.DisplayName;
    public MutationResult SaveRoom(SaveCommand<RoomDto> c, UserDto actor) => Mutate(c, actor, s => { var v = Copy(c.Value); ValidateRoom(s, v); var old = s.Rooms.Find(x => x.Id == v.Id); Version(old, v.Version); v.Version++; History(s, actor, "room", v.Id, old is null ? "Создан кабинет" : "Изменён кабинет", old, v); Save(s.Rooms, v); });
    public MutationResult SeedRooms(SeedRoomsCommand c, UserDto actor) => Mutate(c, actor, s => { foreach (var raw in c.Rooms) { if (s.Rooms.Any(r => string.Equals(r.Building.Trim(), raw.Building.Trim(), StringComparison.OrdinalIgnoreCase) && string.Equals(r.Number.Trim(), raw.Number.Trim(), StringComparison.OrdinalIgnoreCase))) continue; var r = Copy(raw); ValidateRoom(s, r); Require(!s.Rooms.Any(x => x.Id == r.Id), "Повтор идентификатора кабинета."); r.Version = 1; s.Rooms.Add(r); History(s, actor, "room", r.Id, "Добавлен с карты", null, r); } });
    public MutationResult SaveAsset(SaveCommand<AssetDto> c, UserDto actor) => Mutate(c, actor, s => SaveAssetCore(s, c.Value, actor));
    private static void SaveAssetCore(InventorySnapshot s, AssetDto raw, UserDto actor)
    {
        var a = Copy(raw); ValidateAsset(s, a); var old = s.Assets.Find(x => x.Id == a.Id); Version(old, a.Version);
        if (old?.RoomId != a.RoomId) { a.X = null; a.Y = null; } // A transfer starts unplaced in its new room.
        a.Version++; History(s, actor, "asset", a.Id, old is null ? "Добавлено оборудование" : old.RoomId != a.RoomId ? "Перемещено оборудование" : a.Archived && !old.Archived ? "Архивировано оборудование" : "Изменено оборудование", old, a); Save(s.Assets, a);
    }
    public MutationResult Import(ImportCommand c, UserDto actor) => Mutate(c, actor, s =>
    {
        Require(c.FileHash.Length == 64 && c.FileHash.All(Uri.IsHexDigit), "Некорректная SHA-256 сумма файла.");
        if (s.ImportedFileHashes.Contains(c.FileHash, StringComparer.OrdinalIgnoreCase)) return;
        foreach (var raw in c.Rooms) { var r = Copy(raw); ValidateRoom(s, r); var old = s.Rooms.Find(x => x.Id == r.Id); Version(old, r.Version); r.Version++; Save(s.Rooms, r); History(s, actor, "room", r.Id, "Импорт кабинета", old, r); }
        Require(c.Assets.Select(x => x.Id).Distinct().Count() == c.Assets.Count, "В импорт передан один объект несколько раз.");
        foreach (var a in c.Assets) { Require(a.Source is not null, "Не сохранён источник строки импорта."); SaveAssetCore(s, a, actor); }
        s.ImportedFileHashes.Add(c.FileHash);
    });

    public MutationResult SaveCartridge(SaveCommand<CartridgeDto> c, UserDto actor) => Mutate(c, actor, s =>
    {
        var v = Copy(c.Value); RequireGuid(v.Id); Require(!string.IsNullOrWhiteSpace(v.Number) && !string.IsNullOrWhiteSpace(v.Model), "Укажите номер и модель картриджа.");
        Require(!s.Cartridges.Any(x => x.Id != v.Id && string.Equals(x.Number.Trim(), v.Number.Trim(), StringComparison.OrdinalIgnoreCase)), "Номер экземпляра картриджа уже существует.");
        var old = s.Cartridges.Find(x => x.Id == v.Id); Version(old, v.Version);
        Require(v.PrinterId == old?.PrinterId && (old is null || v.Slot == old.Slot), "Для установки или снятия используйте операцию замены картриджа.");
        Require(new[] { "Запас", "Установлен", "На заправке", "Списан" }.Contains(v.Status), "Неизвестное состояние картриджа.");
        Require((v.PrinterId is not null) == (v.Status == "Установлен"), "Состояние должно соответствовать установке картриджа.");
        v.Version++; History(s, actor, "cartridge", v.Id, old is null ? "Создан картридж" : old.Status != v.Status ? $"Картридж: {v.Status}" : "Изменён картридж", old, v); Save(s.Cartridges, v);
    });
    public MutationResult ReplaceCartridge(ReplaceCartridgeCommand c, UserDto actor) => Mutate(c, actor, s =>
    {
        var printer = Existing(s.Assets, c.PrinterId); Version(printer, c.PrinterVersion); Require(!printer.Archived, "Принтер находится в архиве.");
        Require(IsPrinter(printer), "Объект должен иметь тип Принтер или МФУ.");
        Require(!string.IsNullOrWhiteSpace(c.Slot), "Укажите цветовую позицию.");
        var installed = s.Cartridges.Find(x => x.PrinterId == c.PrinterId && string.Equals(x.Slot, c.Slot, StringComparison.OrdinalIgnoreCase));
        if (installed?.Id != c.OldCartridgeId) throw new StoreException(409, "conflict", "Установленный картридж уже изменился.", installed);
        if (installed is not null) Version(installed, c.OldCartridgeVersion);
        Require(c.NewCartridgeId != c.OldCartridgeId || c.NewCartridgeId is null, "Новый и снятый картриджи совпадают.");
        Require(c.NewCartridgeId is not null || installed is not null, "Не выбран картридж.");
        var replacement = c.NewCartridgeId is null ? null : Existing(s.Cartridges, c.NewCartridgeId);
        if (replacement is not null)
        {
            Version(replacement, c.NewCartridgeVersion); Require(replacement.PrinterId is null && replacement.Status == "Запас", "Картридж должен быть в запасе и не установлен в другом принтере.");
            Require(printer.CompatibleCartridgeModels.Count == 0 || printer.CompatibleCartridgeModels.Contains(replacement.Model, StringComparer.OrdinalIgnoreCase), "Модель картриджа отсутствует в списке совместимых моделей принтера.");
        }
        Require(new[] { "Запас", "На заправке", "Списан" }.Contains(c.RemovedStatus), "Некорректное состояние снятого картриджа.");
        if (installed is not null) { var before = Copy(installed); installed.PrinterId = null; installed.Slot = ""; installed.Status = c.RemovedStatus; installed.Version++; History(s, actor, "cartridge", installed.Id, "Снят картридж", before, installed); }
        if (replacement is not null) { var before = Copy(replacement); replacement.PrinterId = c.PrinterId; replacement.Slot = c.Slot; replacement.Status = "Установлен"; replacement.Version++; History(s, actor, "cartridge", replacement.Id, "Установлен картридж", before, replacement); }
        printer.Version++; History(s, actor, "asset", printer.Id, $"Замена картриджа ({c.Slot})", c.OldCartridgeId, c.NewCartridgeId);
    });

    private static string RoomNumber(InventorySnapshot s, string? id) => s.Rooms.Find(x => x.Id == id) is { } r ? $"{r.Number} ({r.Building})" : "Не назначен";
    private static AuditItemDto AuditItem(InventorySnapshot s, AssetDto a) => new() { Version = 1, AssetId = a.Id, AssetSnapshot = Copy(a), ExpectedRoomId = a.RoomId, ExpectedRoomSnapshot = s.Rooms.Find(x => x.Id == a.RoomId) is { } room ? Copy(room) : null, ExpectedRoomNumber = RoomNumber(s, a.RoomId), PhotoCount = s.Photos.Count(x => x.OwnerType == "asset" && x.OwnerId == a.Id), InstalledCartridges = string.Join("; ", s.Cartridges.Where(x => x.PrinterId == a.Id).Select(x => $"{x.Slot}: {x.Number} ({x.Model})")) };
    public MutationResult CreateAudit(SaveCommand<AuditDto> c, UserDto actor) => Mutate(c, actor, s =>
    {
        var a = Copy(c.Value); RequireGuid(a.Id); Require(!s.Audits.Any(x => x.Id == a.Id) && a.Version == 0, "Проверка уже существует."); Require(!string.IsNullOrWhiteSpace(a.Name), "Укажите название проверки.");
        Require(new[] { "building", "floor", "room" }.Contains(a.Scope), "Область проверки: building, floor или room.");
        if (a.Scope == "room") Existing(s.Rooms, a.RoomId ?? ""); if (a.Scope == "floor") Require(a.Floor.HasValue, "Укажите этаж.");
        var roomIds = s.Rooms.Where(r => a.Scope == "room" ? r.Id == a.RoomId : r.Building == a.Building && (a.Scope != "floor" || r.Floor == a.Floor)).Select(r => r.Id).ToHashSet();
        a.Items = s.Assets.Where(x => !x.Archived && x.RoomId is not null && roomIds.Contains(x.RoomId)).Select(x => AuditItem(s, x)).ToList();
        a.Version = 1; a.CompletedUtc = null; a.CreatedBy = Author(actor); s.Audits.Add(a); History(s, actor, "audit", a.Id, "Начата инвентаризация", null, a);
    });
    private static AuditDto EditableAudit(InventorySnapshot s, string id, long version) { var a = Existing(s.Audits, id); Require(a.CompletedUtc is null, "Завершённая инвентаризация неизменяема."); Version(a, version); return a; }
    public MutationResult SaveAuditItem(AuditItemCommand c, UserDto actor) => Mutate(c, actor, s =>
    {
        var audit = EditableAudit(s, c.AuditId, c.AuditVersion); var item = Existing(audit.Items, c.Value.Id); Version(item, c.Value.Version);
        Require(new[] { "Не проверено", "Найдено", "Отсутствует", "Обнаружено в другом кабинете" }.Contains(c.Value.Result), "Неизвестный результат проверки.");
        var before = Copy(item); var actualId = c.Value.Result == "Найдено" ? (item.AddedAfterStart ? c.Value.ActualRoomId ?? item.ActualRoomId : item.ExpectedRoomId) : c.Value.Result == "Обнаружено в другом кабинете" ? c.Value.ActualRoomId : null;
        if (actualId is not null) Existing(s.Rooms, actualId);
        if (c.Value.Result == "Обнаружено в другом кабинете") Require(actualId is not null && actualId != item.ExpectedRoomId, "Укажите другой фактический кабинет.");
        item.Result = c.Value.Result; item.ActualRoomId = actualId; item.ActualRoomNumber = RoomNumber(s, actualId); item.Comment = c.Value.Comment; item.CheckedUtc = item.Result == "Не проверено" ? null : DateTimeOffset.UtcNow; item.CheckedBy = item.Result == "Не проверено" ? "" : Author(actor); item.Version++; audit.Version++;
        History(s, actor, "auditItem", item.Id, "Проверено наличие", before, item);
    });
    public MutationResult AddAuditAsset(AuditAddAssetCommand c, UserDto actor) => Mutate(c, actor, s =>
    {
        var a = EditableAudit(s, c.AuditId, c.Version); Require(!a.Items.Any(x => x.AssetId == c.AssetId), "Оборудование уже включено в проверку."); var asset = Existing(s.Assets, c.AssetId); Require(!asset.Archived, "Оборудование находится в архиве."); var item = AuditItem(s, asset); item.AddedAfterStart = true; item.ExpectedRoomId = null; item.ExpectedRoomSnapshot = null; item.ExpectedRoomNumber = "Не учтено на начало проверки"; item.ActualRoomId = asset.RoomId; item.ActualRoomNumber = RoomNumber(s, asset.RoomId); item.Comment = "Обнаружено при проверке; добавлено дополнительно"; a.Items.Add(item); a.Version++; History(s, actor, "audit", a.Id, "Добавлено обнаруженное оборудование", null, item);
    });
    public MutationResult CompleteAudit(AuditCompleteCommand c, UserDto actor) => Mutate(c, actor, s =>
    {
        var a = EditableAudit(s, c.AuditId, c.Version); a.CompletedUtc = DateTimeOffset.UtcNow; a.Version++;
        foreach (var item in a.Items) item.PhotoCount += s.Photos.Count(p => p.OwnerType == "auditItem" && p.OwnerId == item.Id);
        History(s, actor, "audit", a.Id, "Завершена инвентаризация", null, a);
    });

    private static void ValidatePhotoOwner(InventorySnapshot s, string type, string id)
    {
        switch (type) { case "asset": Existing(s.Assets, id); break; case "room": Existing(s.Rooms, id); break; case "auditItem": var a = s.Audits.Find(x => x.Items.Any(i => i.Id == id)) ?? throw new StoreException(404, "not_found", "Строка проверки не найдена."); Require(a.CompletedUtc is null, "Фотографии завершённой проверки неизменяемы."); break; default: throw new StoreException(400, "invalid", "Тип владельца фото: asset, room, auditItem."); }
    }
    public MutationResult UploadPhoto(string commandId, string ownerType, string ownerId, string caption, byte[] bytes, UserDto actor)
    {
        Require(bytes.Length is > 0 and <= 20 * 1024 * 1024, "Фотография должна быть не более 20 МБ.");
        var png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 && bytes[^2] == 255 && bytes[^1] == 217;
        Require(png || jpeg, "Разрешены только файлы JPG/PNG с корректной сигнатурой."); RequireGuid(commandId);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { commandId, ownerType, ownerId, caption, hash = Convert.ToHexString(SHA256.HashData(bytes)) }, Json))));
        var photoId = Guid.NewGuid().ToString("N"); var path = Path.Combine(PhotoDirectory, photoId);
        lock (gate)
        {
            try { return Mutate(new Command { CommandId = commandId }, actor, s => { ValidatePhotoOwner(s, ownerType, ownerId); var photo = new PhotoDto { Id = photoId, Version = 1, OwnerType = ownerType, OwnerId = ownerId, Caption = caption, IsPrimary = !s.Photos.Any(x => x.OwnerType == ownerType && x.OwnerId == ownerId), ContentType = png ? "image/png" : "image/jpeg", Length = bytes.Length, CreatedUtc = DateTimeOffset.UtcNow }; File.WriteAllBytes(path, bytes); s.Photos.Add(photo); History(s, actor, "photo", photo.Id, "Добавлена фотография", null, photo); }, fingerprint); }
            catch { if (File.Exists(path)) File.Delete(path); throw; }
        }
    }
    public MutationResult SavePhoto(SaveCommand<PhotoDto> c, UserDto actor) => Mutate(c, actor, s =>
    {
        var photo = Existing(s.Photos, c.Value.Id); Version(photo, c.Value.Version); ValidatePhotoOwner(s, photo.OwnerType, photo.OwnerId); var old = Copy(photo); photo.Caption = c.Value.Caption;
        if (c.Value.IsPrimary) foreach (var p in s.Photos.Where(x => x.Id != photo.Id && x.OwnerType == photo.OwnerType && x.OwnerId == photo.OwnerId && x.IsPrimary)) { p.IsPrimary = false; p.Version++; }
        photo.IsPrimary = c.Value.IsPrimary; photo.Version++; History(s, actor, "photo", photo.Id, "Изменена фотография", old, photo);
    });
    public (string Path, string ContentType) Photo(string id)
    {
        RequireGuid(id); var photo = Existing(Snapshot().Photos, id); var path = Path.Combine(PhotoDirectory, photo.Id); if (!File.Exists(path)) throw new StoreException(404, "photo_missing", "Файл фотографии отсутствует в хранилище."); return (path, photo.ContentType);
    }
}
