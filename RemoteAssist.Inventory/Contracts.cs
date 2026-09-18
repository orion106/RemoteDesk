namespace RemoteAssist.Inventory;

public class VersionedEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long Version { get; set; }
}

public sealed class RoomDto : VersionedEntity
{
    public string Building { get; set; } = "Д";
    public int Floor { get; set; } = 1;
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string? MapKey { get; set; }
    public string Kind { get; set; } = "room";
}

public sealed class AssetDto : VersionedEntity
{
    public string? RoomId { get; set; }
    public string Seat { get; set; } = "";
    public double? X { get; set; }
    public double? Y { get; set; }
    public string Type { get; set; } = "Компьютер";
    public string Model { get; set; } = "";
    public string InventoryNumber { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Mac { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string? ParentAssetId { get; set; }
    public string Status { get; set; } = "Исправно";
    public string Description { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> CompatibleCartridgeModels { get; set; } = [];
    public bool Archived { get; set; }
    public ImportSourceDto? Source { get; set; }
}

public sealed class ImportSourceDto
{
    public string SourceId { get; set; } = "";
    public string FileHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sheet { get; set; } = "";
    public int RowNumber { get; set; }
    public string RowSignature { get; set; } = "";
    public string ImportKey { get; set; } = "";
    public Dictionary<string, string> RawCells { get; set; } = [];
    public Dictionary<string, string> RawFormulas { get; set; } = [];
}

public sealed class PhotoDto : VersionedEntity
{
    public string OwnerType { get; set; } = "asset";
    public string OwnerId { get; set; } = "";
    public string Caption { get; set; } = "";
    public bool IsPrimary { get; set; }
    public string ContentType { get; set; } = "image/jpeg";
    public long Length { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class CartridgeDto : VersionedEntity
{
    public string Number { get; set; } = "";
    public string Model { get; set; } = "";
    public string Color { get; set; } = "Чёрный";
    public string Status { get; set; } = "Запас";
    public string? PrinterId { get; set; }
    public string Slot { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class AuditDto : VersionedEntity
{
    public string Name { get; set; } = "";
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;
    public string Scope { get; set; } = "building";
    public string Building { get; set; } = "Д";
    public int? Floor { get; set; }
    public string? RoomId { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public List<AuditItemDto> Items { get; set; } = [];
}

public sealed class AuditItemDto : VersionedEntity
{
    public string AssetId { get; set; } = "";
    public bool AddedAfterStart { get; set; }
    public AssetDto AssetSnapshot { get; set; } = new();
    public string? ExpectedRoomId { get; set; }
    public RoomDto? ExpectedRoomSnapshot { get; set; }
    public string ExpectedRoomNumber { get; set; } = "";
    public string? ActualRoomId { get; set; }
    public string ActualRoomNumber { get; set; } = "";
    public string Result { get; set; } = "Не проверено";
    public string Comment { get; set; } = "";
    public DateTimeOffset? CheckedUtc { get; set; }
    public string CheckedBy { get; set; } = "";
    public int PhotoCount { get; set; }
    public string InstalledCartridges { get; set; } = "";
}

public sealed class HistoryDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public DateTimeOffset DateUtc { get; set; }
    public string Author { get; set; } = "";
    public string Action { get; set; } = "";
    public string BeforeJson { get; set; } = "";
    public string AfterJson { get; set; } = "";
}

public sealed class UserDto : VersionedEntity
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "editor";
    public bool Disabled { get; set; }
}

public sealed class InventorySnapshot
{
    public string InstanceId { get; set; } = "";
    public long Revision { get; set; }
    public DateTimeOffset RetrievedUtc { get; set; }
    public List<RoomDto> Rooms { get; set; } = [];
    public List<AssetDto> Assets { get; set; } = [];
    public List<PhotoDto> Photos { get; set; } = [];
    public List<CartridgeDto> Cartridges { get; set; } = [];
    public List<AuditDto> Audits { get; set; } = [];
    public List<HistoryDto> History { get; set; } = [];
    public List<string> ImportedFileHashes { get; set; } = [];
}

public class Command { public string CommandId { get; set; } = Guid.NewGuid().ToString("N"); }
public sealed class SaveCommand<T> : Command { public T Value { get; set; } = default!; }
public sealed class LoginRequest { public string Username { get; set; } = ""; public string Password { get; set; } = ""; }
public sealed class LoginResponse { public string Token { get; set; } = ""; public DateTimeOffset ExpiresUtc { get; set; } public UserDto User { get; set; } = new(); }
public sealed class SaveUserCommand : Command { public UserDto Value { get; set; } = new(); public string? Password { get; set; } }
public sealed class SeedRoomsCommand : Command { public List<RoomDto> Rooms { get; set; } = []; }
public sealed class ImportCommand : Command
{
    public string FileHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public List<RoomDto> Rooms { get; set; } = [];
    public List<AssetDto> Assets { get; set; } = [];
}
public sealed class ReplaceCartridgeCommand : Command
{
    public string PrinterId { get; set; } = "";
    public long PrinterVersion { get; set; }
    public string Slot { get; set; } = "Чёрный";
    public string? NewCartridgeId { get; set; }
    public long NewCartridgeVersion { get; set; }
    public string? OldCartridgeId { get; set; }
    public long OldCartridgeVersion { get; set; }
    public string RemovedStatus { get; set; } = "На заправке";
}
public sealed class AuditItemCommand : Command
{
    public string AuditId { get; set; } = "";
    public long AuditVersion { get; set; }
    public AuditItemDto Value { get; set; } = new();
}
public sealed class AuditCompleteCommand : Command { public string AuditId { get; set; } = ""; public long Version { get; set; } }
public sealed class AuditAddAssetCommand : Command { public string AuditId { get; set; } = ""; public long Version { get; set; } public string AssetId { get; set; } = ""; }
public sealed class ApiError { public string Code { get; set; } = ""; public string Message { get; set; } = ""; public object? Current { get; set; } }
public sealed class MutationResult { public long Revision { get; set; } public bool AlreadyApplied { get; set; } }
