using System.Text.Json.Serialization;

namespace RemoteAssist;

public enum LabOs { Unknown, Windows, Astra }
public enum JobState { Queued, Running, Succeeded, Deferred, Failed, Uncertain, Cancelled, RebootRequired, Missed }
public enum LabAction { Wake, WakeWindows, Shutdown, RestartAstra, BootWindows, Install, Cleanup, RestartApplication }

public sealed class LabOptions
{
    public string GlpiUrl { get; set; } = "";
    public string GlpiEntity { get; set; } = "";
    public string SshUser { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshPrivateKey { get; set; } = "";
    public string ViewerPath { get; set; } = "";
    public string StudentMasks { get; set; } = "";
    public string ProtectedUsers { get; set; } = "admin\nAdministrator\nАдминистратор";
    public string WindowsServices { get; set; } = "glpi-agent";
    public string AstraServices { get; set; } = "glpi-agent";
    public bool Monitoring { get; set; }
    public int PollSeconds { get; set; } = 60;
    public int LowDiskPercent { get; set; } = 10;
    public LabOptions Copy() => JsonSerializer.Deserialize<LabOptions>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        if (GlpiUrl.Length > 0 && (!Uri.TryCreate(GlpiUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
            throw new InvalidOperationException("Для GLPI требуется HTTPS с доверенным сертификатом.");
        if (GlpiEntity.Length > 0 && !int.TryParse(GlpiEntity, out _)) throw new InvalidOperationException("Область GLPI — числовой ID сущности.");
        if (SshPort is < 1 or > 65535 || PollSeconds < 15 || LowDiskPercent is < 1 or > 90) throw new InvalidOperationException("Проверьте порт SSH, интервал и порог свободного места.");
        if (Lines(StudentMasks).Any(x => x.All(c => c is '*' or '?'))) throw new InvalidOperationException("Маска студентов должна содержать часть логина; универсальная маска запрещена.");
    }
    public static string[] Lines(string value) => value.Split(['\r', '\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public sealed class LabComputer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Room { get; set; } = "Без аудитории";
    public string Seat { get; set; } = "";
    public int Position { get; set; }
    public bool LocalPlacement { get; set; }
    public string Uuid { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public List<string> GlpiIds { get; set; } = [];
    public List<string> MacAddresses { get; set; } = [];
    public string Broadcast { get; set; } = "255.255.255.255";
    public List<OsEndpoint> Systems { get; set; } = [];
    public string WindowsMenuId { get; set; } = "";
    public bool BootPilotVerified { get; set; }
    public string BootConfigHash { get; set; } = "";
    public string InventoryNote { get; set; } = "";
    public DateTimeOffset? ExpectedOffUntil { get; set; }
    public LabComputer Copy() => JsonSerializer.Deserialize<LabComputer>(JsonSerializer.Serialize(this))!;
}
public sealed class OsEndpoint
{
    public LabOs Os { get; set; }
    public string Address { get; set; } = "";
    public string Description { get; set; } = "";
    public string SshFingerprint { get; set; } = "";
    public DateTimeOffset? InventoryAt { get; set; }
    public MachineSnapshot? LastSnapshot { get; set; }
}
public sealed class MachineSnapshot
{
    public LabOs Os { get; set; }
    public string Address { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string Serial { get; set; } = "";
    public string HostName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string BootId { get; set; } = "";
    public string User { get; set; } = "";
    public string State { get; set; } = "Не проверен";
    public string Detail { get; set; } = "";
    public bool Reachable { get; set; }
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<LabDisk> Disks { get; set; } = [];
    public List<LabService> Services { get; set; } = [];
    public List<LabProcess> Processes { get; set; } = [];
    public Dictionary<string, string> Hardware { get; set; } = [];
    public Dictionary<string, string> Diagnostics { get; set; } = [];
}
public sealed record LabDisk(string Name, long Size, long Free, string Health = "Нет данных")
{
    public double FreePercent => Size <= 0 ? 0 : 100.0 * Free / Size;
}
public sealed record LabService(string Name, string State);
public sealed record LabProcess(int Id, string Name, string Path, string User, string Session, string Created);
public sealed class StudentProfile : ObservableObject
{
    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string ComputerId { get; set; } = "";
    public string Computer { get; set; } = "";
    public string Sid { get; set; } = "";
    public string Login { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Loaded { get; set; }
    public bool Special { get; set; }
    public bool? Administrator { get; set; }
    public bool LocalPathSafe { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset PreviewAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Eligible => Reason.Length == 0;
}
public sealed class SoftwarePackage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public LabOs Os { get; set; } = LabOs.Windows;
    public string Architecture { get; set; } = "x64";
    public string Version { get; set; } = "";
    public string Kind { get; set; } = "MSI";
    public string Source { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Detection { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool SilentNoRestartVerified { get; set; }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Version) || string.IsNullOrWhiteSpace(Detection)) throw new InvalidOperationException("Укажите название, точную версию и правило проверки (путь EXE Windows или имя пакета Astra).");
        if (Architecture is not ("x64" or "x86" or "arm64" or "any")) throw new InvalidOperationException("Архитектура: x64, x86, arm64 или any.");
        if (Os == LabOs.Windows && Kind is not ("MSI" or "EXE") || Os == LabOs.Astra && Kind is not ("DEB" or "APT") || Os == LabOs.Unknown) throw new InvalidOperationException("Тип пакета не соответствует ОС.");
        if (Kind == "EXE" && (!SilentNoRestartVerified || string.IsNullOrWhiteSpace(Arguments))) throw new InvalidOperationException("Для EXE нужны проверенные параметры тихой установки без перезагрузки.");
        if (Kind == "APT" && !Regex.IsMatch(Source, @"^[a-z0-9][a-z0-9+.-]*$")) throw new InvalidOperationException("APT: укажите одно имя пакета без команд и опций.");
        if (Os == LabOs.Astra && !Regex.IsMatch(Detection, @"^[a-z0-9][a-z0-9+.-]*$")) throw new InvalidOperationException("Проверка Astra: имя пакета dpkg.");
        if (Kind != "APT" && (!File.Exists(Source) || Sha256.Length != 64)) throw new InvalidOperationException("Выберите файл пакета и рассчитайте SHA-256.");
    }
}
public sealed class AllowedApplication
{
    public string Name { get; set; } = "";
    public LabOs Os { get; set; } = LabOs.Windows;
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
}
public sealed class AdminJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ComputerId { get; set; } = "";
    public string Computer { get; set; } = "";
    public LabAction Action { get; set; }
    public JobState State { get; set; } = JobState.Queued;
    public string Detail { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public LabOs ExecutedOs { get; set; }
    public string Address { get; set; } = "";
    public string BootBefore { get; set; } = "";
    public string Payload { get; set; } = "";
    public string StateText => LabText.Job(State);
    public string ActionText => LabText.Action(Action);
}
public sealed class LabSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> ComputerIds { get; set; } = [];
    public LabAction Action { get; set; }
    public DateTimeOffset NextRun { get; set; }
    public bool Weekly { get; set; }
    public bool Enabled { get; set; } = true;
}
public sealed class LabAlert
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ComputerId { get; set; } = "";
    public string Computer { get; set; } = "";
    public string Key { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public string StateText => ResolvedAt is null ? "Открыто" : "Устранено";
}
public sealed record OperationResult(JobState State, string Detail);
public sealed class RemoteReply
{
    public string State { get; set; } = "Succeeded";
    public string Detail { get; set; } = "";
    public JsonElement Data { get; set; }
    public OperationResult Result => new(Enum.TryParse<JobState>(State, true, out var state) ? state : JobState.Uncertain, Detail);
}
public static class LabText
{
    public static string Os(LabOs os) => os switch { LabOs.Windows => "Windows", LabOs.Astra => "Astra", _ => "Не определена" };
    public static string Job(JobState state) => state switch { JobState.Queued => "В очереди", JobState.Running => "Выполняется", JobState.Succeeded => "Выполнено", JobState.Deferred => "Отложено", JobState.Failed => "Ошибка", JobState.Uncertain => "Результат не подтверждён", JobState.Cancelled => "Отменено", JobState.RebootRequired => "Нужна перезагрузка → Astra", _ => "Пропущено" };
    public static string Action(LabAction action) => action switch { LabAction.Wake => "Включить → Astra", LabAction.WakeWindows => "Включить → Windows", LabAction.Shutdown => "Выключить", LabAction.RestartAstra => "Перезагрузить → Astra", LabAction.BootWindows => "Загрузить Windows", LabAction.Install => "Установка ПО", LabAction.Cleanup => "Очистка профилей", _ => "Перезапуск приложения" };
}
