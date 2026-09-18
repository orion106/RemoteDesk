using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet.Common;

namespace RemoteAssist;

public interface IOperationQueue
{
    Task EnqueueAsync(LabComputer pc, LabAction action, object? payload, CancellationToken token);
    Task ReconcileAsync(AdminJob job, CancellationToken token);
    Task StopAsync();
}

public sealed class LabRuntime : IOperationQueue, IDisposable
{
    private readonly LabStore _store;
    private readonly DirectoryConfiguration _configuration;
    private readonly Dictionary<LabOs, ISystemAdapter> _adapters;
    private readonly SemaphoreSlim _pollGate = new(1, 1), _operationSlots = new(4, 4);
    private readonly ConcurrentDictionary<string, int> _failures = new();
    private readonly ConcurrentDictionary<string, Task> _running = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly Func<string, int, CancellationToken, Task<bool>>? _transportCheck;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
    public List<LabComputer> Computers { get; private set; }
    public List<AdminJob> Jobs { get; }
    public List<LabSchedule> Schedules { get; }
    public List<LabAlert> Alerts { get; }
    public ConcurrentDictionary<string, MachineSnapshot> Snapshots { get; } = new();
    public event Action? Changed;
    public bool IsBusy => !_running.IsEmpty;
    public LabRuntime(LabStore store, DirectoryConfiguration configuration, IEnumerable<ISystemAdapter> adapters, Func<string, int, CancellationToken, Task<bool>>? transportCheck = null)
    {
        _store = store; _configuration = configuration; _adapters = adapters.ToDictionary(x => x.Os);
        _transportCheck = transportCheck;
        Computers = store.Load<LabComputer>("computer"); Jobs = store.Load<AdminJob>("job"); Schedules = store.Load<LabSchedule>("schedule"); Alerts = store.Load<LabAlert>("alert");
        foreach (var job in Jobs.Where(x => x.State is JobState.Running or JobState.Queued)) { job.State = JobState.Uncertain; job.Detail = "Приложение было закрыто. Сначала выполните сверку; автоматического повтора нет."; store.Save("job", job.Id, job); }
        foreach (var job in Jobs.Where(BlocksMutations)) { var pc = Computers.FirstOrDefault(x => x.Id == job.ComputerId); if (pc is not null) MachineMutationGate.SetUnconfirmed(pc, job.Id, true); }
        foreach (var schedule in Schedules.Where(x => x.Enabled && x.NextRun <= DateTimeOffset.Now)) SkipSchedule(schedule);
    }
    public void SaveComputers() { lock (_sync) _store.ReplaceComputers(Computers); Changed?.Invoke(); }
    public void Import(IEnumerable<LabComputer> incoming) { lock (_sync) { Computers = MachineIdentity.MergeInventory(Computers, incoming); _store.ReplaceComputers(Computers); } Changed?.Invoke(); }
    public LabComputer? FindComputer(string id) { lock (_sync) return Computers.FirstOrDefault(x => x.Id == id)?.Copy(); }
    public void SaveComputer(LabComputer computer)
    {
        lock (_sync)
        {
            var replacement = Computers.Where(x => x.Id != computer.Id).Append(computer.Copy()).ToList();
            _store.ReplaceComputers(replacement); Computers = replacement;
        }
        Changed?.Invoke();
    }
    public void ApplyInventoryPlacements(IReadOnlyDictionary<string, InventoryPlacementProjection?> placements)
    {
        if (placements.Count == 0) return;
        var changed = false;
        lock (_sync)
        {
            var updates = new Dictionary<string, LabComputer>(StringComparer.Ordinal);
            foreach (var current in Computers)
            {
                if (!placements.TryGetValue(current.Id, out var placement)) continue;
                var room = placement is null ? current.Room : string.IsNullOrWhiteSpace(placement.RoomDisplay) ? "Без аудитории" : placement.RoomDisplay;
                var seat = placement?.Seat ?? current.Seat;
                if (current.LocalPlacement && current.Room == room && current.Seat == seat) continue;
                var computer = current.Copy(); computer.LocalPlacement = true; computer.Room = room; computer.Seat = seat;
                updates.Add(computer.Id, computer);
            }
            if (updates.Count > 0)
            {
                var replacement = Computers.Select(x => updates.GetValueOrDefault(x.Id) ?? x).ToList();
                _store.ReplaceComputers(replacement); Computers = replacement; changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }
    public void MergeComputers(string primaryId, string secondaryId, IReadOnlyList<InventoryComputerLink>? inventoryLinks = null, string? placementSourceId = null)
    {
        if (primaryId == secondaryId) throw new InvalidOperationException("Выберите два разных компьютера.");
        lock (_sync)
        {
            var primary = Computers.Single(x => x.Id == primaryId).Copy();
            var secondary = Computers.Single(x => x.Id == secondaryId);
            if (!MachineIdentity.SameHardware(primary, secondary)) throw new InvalidOperationException("Сначала сохраните одинаковые подтверждённые аппаратные ID в обеих карточках.");
            if (Jobs.Any(x => (x.ComputerId == primaryId || x.ComputerId == secondaryId) && x.State is JobState.Queued or JobState.Running or JobState.Uncertain))
                throw new InvalidOperationException("Сначала сверьте незавершённые задания объединяемых компьютеров.");
            MachineIdentity.MergeInto(primary, secondary);
            if (placementSourceId is not null)
            {
                if (placementSourceId != primaryId && placementSourceId != secondaryId) throw new InvalidOperationException("Источник размещения не относится к объединяемым ПК.");
                var placementSource = Computers.Single(x => x.Id == placementSourceId);
                primary.Room = placementSource.Room; primary.Seat = placementSource.Seat; primary.LocalPlacement = true;
            }
            var computers = Computers.Where(x => x.Id != primaryId && x.Id != secondaryId).Append(primary).ToList();
            var schedules = Schedules.Select(x => JsonSerializer.Deserialize<LabSchedule>(JsonSerializer.Serialize(x))!).ToList();
            foreach (var schedule in schedules) schedule.ComputerIds = schedule.ComputerIds.Select(x => x == secondaryId ? primaryId : x).Distinct().ToList();
            _store.ReplaceComputerState(computers, schedules, inventoryLinks);
            Computers = computers; Schedules.Clear(); Schedules.AddRange(schedules);
            Snapshots.TryRemove(secondaryId, out _);
        }
        Changed?.Invoke();
    }
    public void SaveSchedule(LabSchedule schedule) { lock (_sync) { if (!Schedules.Any(x => x.Id == schedule.Id)) Schedules.Add(schedule); _store.Save("schedule", schedule.Id, schedule); } Changed?.Invoke(); }
    public void RemoveSchedule(LabSchedule schedule) { lock (_sync) { Schedules.Remove(schedule); _store.Delete("schedule", schedule.Id); } Changed?.Invoke(); }
    private void SkipSchedule(LabSchedule schedule)
    {
        if (schedule.Weekly) { do { schedule.NextRun = NextWeek(schedule.NextRun); } while (schedule.NextRun <= DateTimeOffset.Now); } else schedule.Enabled = false;
        _store.Save("schedule", schedule.Id, schedule);
        var job = new AdminJob { Computer = schedule.Name, State = JobState.Missed, Action = schedule.Action, Detail = "Расписание пропущено, пока программа не работала. Автоматического выполнения задним числом нет." }; Jobs.Add(job); _store.Save("job", job.Id, job);
    }
    public static DateTimeOffset NextWeek(DateTimeOffset date)
    {
        var local = date.LocalDateTime.AddDays(7); return new(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
    public async Task TickAsync(CancellationToken token)
    {
        var now = DateTimeOffset.Now; LabSchedule[] due;
        lock (_sync) due = Schedules.Where(x => x.Enabled && x.NextRun <= now).ToArray();
        foreach (var schedule in due)
        {
            if (now - schedule.NextRun > TimeSpan.FromSeconds(30)) { lock (_sync) SkipSchedule(schedule); Changed?.Invoke(); continue; }
            var pcIds = schedule.ComputerIds.ToArray();
            lock (_sync) { if (schedule.Weekly) schedule.NextRun = NextWeek(schedule.NextRun); else schedule.Enabled = false; _store.Save("schedule", schedule.Id, schedule); }
            foreach (var id in pcIds) { var pc = Computers.FirstOrDefault(x => x.Id == id); if (pc is not null) _ = EnqueueAsync(pc, schedule.Action, null, _lifetime.Token); }
        }
        if (_configuration.Settings.Lab.Monitoring && now - _lastPoll >= TimeSpan.FromSeconds(_configuration.Settings.Lab.PollSeconds)) await PollAsync(token);
    }
    public async Task PollAsync(CancellationToken token)
    {
        if (!await _pollGate.WaitAsync(0, token)) return;
        try
        {
            _lastPoll = DateTimeOffset.Now; var computers = Computers.Select(x => x.Copy()).ToArray(); using var gate = new SemaphoreSlim(8);
            await Task.WhenAll(computers.Select(async pc => { await gate.WaitAsync(token); try { await ProbeAsync(pc, token); } finally { gate.Release(); } }));
        }
        finally { _pollGate.Release(); Changed?.Invoke(); }
    }
    public async Task<MachineSnapshot> ProbeAsync(LabComputer pc, CancellationToken token, bool details = false)
    {
        MachineSnapshot? result = null; var errors = new List<string>(); var networkSeen = false;
        // Probe both platforms at every distinct address: inventory and an open TCP port are not OS evidence.
        foreach (var endpoint in pc.Systems.GroupBy(x => x.Address, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            foreach (var os in new[] { endpoint.Os, LabOs.Astra, LabOs.Windows }.Where(x => x != LabOs.Unknown).Distinct())
            {
                token.ThrowIfCancellationRequested(); if (!_adapters.TryGetValue(os, out var adapter)) continue;
                var candidate = pc.Systems.FirstOrDefault(x => x.Address.Equals(endpoint.Address, StringComparison.OrdinalIgnoreCase) && x.Os == os) ?? new OsEndpoint { Address = endpoint.Address, Os = os, SshFingerprint = endpoint.SshFingerprint };
                if (os == LabOs.Astra && candidate.SshFingerprint.Length == 0) { errors.Add("SSH: отпечаток не задан"); continue; }
                var port = os == LabOs.Astra ? _configuration.Settings.Lab.SshPort : 135;
                try
                {
                    if (_transportCheck is not null) { if (!await _transportCheck(candidate.Address, port, token)) { errors.Add(LabText.Os(os) + ": транспорт недоступен"); continue; } }
                    else { using var socket = new System.Net.Sockets.TcpClient(); using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); connectTimeout.CancelAfter(TimeSpan.FromSeconds(2)); await socket.ConnectAsync(candidate.Address, port, connectTimeout.Token); }
                    networkSeen = true;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { errors.Add(LabText.Os(os) + ": транспорт не отвечает"); continue; }
                catch (System.Net.Sockets.SocketException) { errors.Add(LabText.Os(os) + ": транспорт недоступен"); continue; }
                try
                {
                    var snapshot = await adapter.ProbeAsync(pc, candidate, token);
                    if (!MachineIdentity.Matches(pc, snapshot)) { snapshot.Reachable = false; snapshot.State = "Идентичность не подтверждена"; snapshot.Detail = $"Получено: UUID={snapshot.Uuid}; Serial={snapshot.Serial}. Проверьте и сохраните аппаратные ID в карточке."; result = snapshot; continue; }
                    if (details)
                    {
                        var detailed = await adapter.RunAsync(pc, candidate, "Probe", new { Details = true }, Guid.NewGuid().ToString("N"), token);
                        if (detailed.Result.State != JobState.Succeeded) throw new InvalidOperationException(detailed.Detail);
                        snapshot = detailed.Data.Deserialize<MachineSnapshot>(LabWire.Json) ?? throw new InvalidDataException("Нет подробного состояния."); snapshot.Address = candidate.Address;
                        if (!MachineIdentity.Matches(pc, snapshot)) throw new InvalidOperationException("Идентичность изменилась во время диагностики.");
                    }
                    else if (Snapshots.TryGetValue(pc.Id, out var previous) && previous.Os == snapshot.Os && previous.BootId == snapshot.BootId) snapshot.Processes = previous.Processes;
                    result = snapshot;
                    lock (_sync)
                    {
                        var stored = Computers.FirstOrDefault(x => x.Id == pc.Id);
                        if (stored is not null)
                        {
                            var system = stored.Systems.FirstOrDefault(x => x.Os == os && x.Address.Equals(candidate.Address, StringComparison.OrdinalIgnoreCase));
                            if (system is null) { system = new() { Os = os, Address = candidate.Address, SshFingerprint = candidate.SshFingerprint }; stored.Systems.Add(system); }
                            system.LastSnapshot = snapshot; _store.Save("computer", stored.Id, stored);
                        }
                    }
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add(LabText.Os(os) + ": " + Error(ex)); }
            }
            if (result?.Reachable == true) break;
        }
        if (result is null)
        {
            var ping = false;
            foreach (var address in (_transportCheck is null ? pc.Systems.Select(x => x.Address).Distinct().Take(3) : []))
                try { using var p = new Ping(); ping |= (await p.SendPingAsync(address, 1500)).Status == IPStatus.Success; } catch (PingException) { } catch (ArgumentException) { }
            result = new() { State = networkSeen || ping ? "Сеть доступна; управление недоступно" : "Нет ответа сети/управления", Detail = string.Join("; ", errors.Distinct()), Reachable = false };
        }
        result.CheckedAt = DateTimeOffset.UtcNow; Snapshots[pc.Id] = result;
        UpdateAlerts(pc, result); Changed?.Invoke(); return result;
    }
    public static string Error(Exception ex) => ex switch
    {
        UnauthorizedAccessException or SshAuthenticationException => "Отказ авторизации",
        System.Management.ManagementException m when m.ErrorCode == System.Management.ManagementStatus.AccessDenied => "Отказ авторизации WMI",
        SshConnectionException => ex.Message,
        _ => ex.Message
    };
    private void UpdateAlerts(LabComputer pc, MachineSnapshot snapshot)
    {
        var issues = new Dictionary<string, string>();
        var failures = snapshot.Reachable ? 0 : _failures.AddOrUpdate(pc.Id, 1, (_, n) => n + 1);
        if (snapshot.Reachable) _failures[pc.Id] = 0;
        if (!snapshot.Reachable && failures >= 3 && !(pc.ExpectedOffUntil > DateTimeOffset.UtcNow)) issues["unreachable"] = snapshot.State + ": " + snapshot.Detail;
        if (snapshot.Reachable)
        {
            foreach (var disk in snapshot.Disks.Where(x => x.Size > 0 && x.FreePercent < _configuration.Settings.Lab.LowDiskPercent)) issues["disk:" + snapshot.Os + ":" + disk.Name] = $"{disk.Name}: свободно {disk.FreePercent:F1}%";
            foreach (var disk in snapshot.Disks.Where(x => x.Health.Contains("FAILED", StringComparison.OrdinalIgnoreCase) || x.Health.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase) || x.Health.Contains("Warning", StringComparison.OrdinalIgnoreCase))) issues["health:" + snapshot.Os + ":" + disk.Name] = disk.Name + ": " + disk.Health;
            foreach (var service in snapshot.Services.Where(x => x.State is not ("Running" or "active"))) issues["service:" + snapshot.Os + ":" + service.Name] = service.Name + ": " + service.State;
        }
        lock (_sync)
        {
            foreach (var issue in issues)
                if (!Alerts.Any(x => x.ComputerId == pc.Id && x.Key == issue.Key && x.ResolvedAt is null)) { var alert = new LabAlert { ComputerId = pc.Id, Computer = pc.Name, Key = issue.Key, Message = issue.Value }; Alerts.Add(alert); _store.Save("alert", alert.Id, alert); }
            foreach (var alert in Alerts.Where(x => x.ComputerId == pc.Id && x.ResolvedAt is null && !issues.ContainsKey(x.Key)))
            {
                // A missing OS cannot prove that its disk/service problem was fixed.
                if (alert.Key != "unreachable" && (!snapshot.Reachable || !alert.Key.Contains(":" + snapshot.Os + ":"))) continue;
                if (alert.Key == "unreachable" && !snapshot.Reachable && !(pc.ExpectedOffUntil > DateTimeOffset.UtcNow)) continue;
                alert.ResolvedAt = DateTimeOffset.UtcNow; _store.Save("alert", alert.Id, alert);
            }
        }
    }
    public OsEndpoint Endpoint(LabComputer pc, MachineSnapshot state) => pc.Systems.FirstOrDefault(x => x.Os == state.Os && x.Address.Equals(state.Address, StringComparison.OrdinalIgnoreCase)) ?? new() { Os = state.Os, Address = state.Address, SshFingerprint = pc.Systems.FirstOrDefault(x => x.Address == state.Address)?.SshFingerprint ?? "" };
    public async Task<StudentProfile[]> ProfilesAsync(LabComputer pc, CancellationToken token)
    {
        using var lease = await MachineMutationGate.AcquireComputerAsync(pc, token);
        var state = await ProbeAsync(pc, token); if (!state.Reachable || state.Os != LabOs.Windows) throw new InvalidOperationException(pc.Name + ": требуется доступная Windows");
        var reply = await _adapters[LabOs.Windows].RunAsync(pc, Endpoint(pc, state), "Profiles", new { }, Guid.NewGuid().ToString("N"), token);
        if (reply.Result.State != JobState.Succeeded) throw new InvalidOperationException(reply.Detail);
        var profiles = reply.Data.Deserialize<StudentProfile[]>(LabWire.Json) ?? [];
        foreach (var p in profiles) { p.ComputerId = pc.Id; p.Computer = pc.Name; p.Reason = ProfilePolicy.Reason(p, _configuration.Settings.Lab); }
        return profiles;
    }
    public Task EnqueueAsync(LabComputer pc, LabAction action, object? payload, CancellationToken token)
    {
        var job = new AdminJob { ComputerId = pc.Id, Computer = pc.Name, Action = action, Payload = payload is null ? "" : JsonSerializer.Serialize(payload, LabWire.Json) };
        lock (_sync) { Jobs.Add(job); _store.Save("job", job.Id, job); }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running[job.Id] = completion.Task;
        _ = ExecuteTracked(pc.Copy(), job, payload, token, completion); Changed?.Invoke(); return completion.Task;
    }
    private async Task ExecuteTracked(LabComputer pc, AdminJob job, object? payload, CancellationToken token, TaskCompletionSource completion)
    {
        var dispatched = false;
        try
        {
            await _operationSlots.WaitAsync(token);
            try
            {
                var waking = job.Action is LabAction.Wake or LabAction.WakeWindows;
                using var lease = await MachineMutationGate.AcquireComputerAsync(pc, token, reconciliation: waking);
                var pending = Jobs.Where(x => x.Id != job.Id && BlocksMutations(x) && (x.ComputerId == pc.Id || Computers.Any(c => c.Id == x.ComputerId && MachineIdentity.SameHardware(pc, c)))).ToArray();
                if (pending.Any(x => !waking || x.Action is LabAction.Install or LabAction.Cleanup or LabAction.RestartApplication))
                { SetJob(job, JobState.Deferred, "На ПК есть неподтверждённая изменяющая операция. Сначала выполните сверку её результата."); return; }
                SetJob(job, JobState.Running, "Проверка компьютера…");
                if (job.Action is LabAction.Wake or LabAction.WakeWindows)
                {
                    await WakeOnLan.SendAsync(pc, token); pc.ExpectedOffUntil = null;
                    var stored = Computers.FirstOrDefault(x => x.Id == pc.Id); if (stored is not null) { stored.ExpectedOffUntil = null; _store.Save("computer", stored.Id, stored); }
                    var awake = await WaitForOs(pc, LabOs.Astra, null, token);
                    if (awake is null) { SetJob(job, JobState.Uncertain, "Wake-on-LAN отправлен; загрузка Astra не подтверждена."); return; }
                    foreach (var prior in pending.Where(x => x.BootBefore.Length > 0 && x.BootBefore != awake.BootId))
                        SetJob(prior, JobState.Failed, "После пробуждения подтверждена новая загрузка; прежняя команда питания больше не выполняется. Её исторический результат не подтверждён.");
                    if (job.Action == LabAction.Wake) { SetJob(job, JobState.Succeeded, "Загрузка Astra подтверждена."); return; }
                    if (pending.Any(BlocksMutations)) { SetJob(job, JobState.Deferred, "ПК ответил из прежней загрузки. Сначала сверьте неподтверждённую команду питания."); return; }
                }
                var snapshot = await ProbeAsync(pc, token);
                if (!snapshot.Reachable) { SetJob(job, JobState.Deferred, snapshot.State + ": " + snapshot.Detail); return; }
                job.ExecutedOs = snapshot.Os; job.Address = snapshot.Address; job.BootBefore = snapshot.BootId;
                var adapter = _adapters[snapshot.Os]; var endpoint = Endpoint(pc, snapshot);
                string operation; object arguments;
                if (job.Action == LabAction.Install)
                {
                    var package = ((PackagePayload)payload!).Package; package.Validate();
                    if (package.Os != snapshot.Os) { SetJob(job, JobState.Deferred, "Требуется загрузить " + LabText.Os(package.Os)); return; }
                    operation = "Install"; arguments = payload;
                }
                else if (job.Action == LabAction.Cleanup)
                {
                    if (snapshot.Os != LabOs.Windows) { SetJob(job, JobState.Deferred, "Требуется загрузить Windows"); return; }
                    var profiles = ((CleanupPayload)payload!).Profiles;
                    if (profiles.Length == 0 || profiles.Any(x => x.ComputerId != pc.Id || ProfilePolicy.Reason(x, _configuration.Settings.Lab).Length > 0)) throw new InvalidOperationException("Набор профилей не прошёл проверку.");
                    operation = "Cleanup"; arguments = payload;
                }
                else if (job.Action == LabAction.RestartApplication)
                {
                    var restart = (RestartPayload)payload!;
                    if (restart.Application.Os != snapshot.Os || restart.Process.Path != restart.Application.Path) throw new InvalidOperationException("Приложение не соответствует ОС/процессу.");
                    operation = "RestartApplication"; arguments = payload!;
                }
                else
                {
                    var action = job.Action is LabAction.BootWindows or LabAction.WakeWindows ? "BootWindows" : job.Action.ToString();
                    if (action == "BootWindows" && snapshot.Os == LabOs.Windows) { SetJob(job, JobState.Succeeded, "Windows уже загружена; перезагрузка не выполнялась."); return; }
                    operation = "Power"; arguments = new PowerPayload(action, pc.WindowsMenuId, pc.BootConfigHash, pc.BootPilotVerified);
                }
                token.ThrowIfCancellationRequested(); _store.Save("job", job.Id, job); dispatched = true;
                var reply = await adapter.RunAsync(pc, endpoint, operation, arguments, job.Id, token);
                SetJob(job, reply.Result.State, reply.Detail);
                if (operation == "Power" && reply.Result.State == JobState.Uncertain && !token.IsCancellationRequested)
                {
                    if (job.Action == LabAction.Shutdown)
                    {
                        var stored = Computers.FirstOrDefault(x => x.Id == pc.Id); if (stored is not null) { stored.ExpectedOffUntil = DateTimeOffset.UtcNow.AddHours(24); _store.Save("computer", stored.Id, stored); }
                        SetJob(job, JobState.Uncertain, "Выключение запрошено; отсутствие ответа не доказывает отключение питания. Ожидаемая недоступность — 24 часа.");
                    }
                    else
                    {
                        var os = job.Action is LabAction.BootWindows or LabAction.WakeWindows ? LabOs.Windows : LabOs.Astra;
                        var boot = await WaitForOs(pc, os, snapshot.BootId, token);
                        SetJob(job, boot is null ? JobState.Uncertain : JobState.Succeeded, boot is null ? "Загрузка не подтверждена за пять минут. Автоматического повтора нет." : "Загрузка " + LabText.Os(os) + " подтверждена.");
                    }
                }
            }
            finally { _operationSlots.Release(); }
        }
        catch (OperationCanceledException) { SetJob(job, dispatched ? JobState.Uncertain : JobState.Cancelled, dispatched ? "Отмена после отправки. Требуется сверка результата." : "Отменено до отправки команды."); }
        catch (Exception ex) { SetJob(job, dispatched ? JobState.Uncertain : JobState.Failed, Error(ex)); }
        finally { _running.TryRemove(job.Id, out _); completion.TrySetResult(); Changed?.Invoke(); }
    }
    private async Task<MachineSnapshot?> WaitForOs(LabComputer pc, LabOs target, string? previousBoot, CancellationToken token)
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(5);
        do { token.ThrowIfCancellationRequested(); var snapshot = await ProbeAsync(pc, token); if (snapshot.Reachable && snapshot.Os == target && (previousBoot is null || snapshot.BootId != previousBoot)) return snapshot; await Task.Delay(TimeSpan.FromSeconds(5), token); } while (DateTimeOffset.UtcNow < until);
        return null;
    }
    private void SetJob(AdminJob job, JobState state, string detail)
    {
        lock (_sync)
        {
            job.State = state; job.Detail = detail; job.UpdatedAt = DateTimeOffset.UtcNow; _store.Save("job", job.Id, job);
            var pc = Computers.FirstOrDefault(x => x.Id == job.ComputerId); if (pc is not null) MachineMutationGate.SetUnconfirmed(pc, job.Id, BlocksMutations(job));
        }
        Changed?.Invoke();
    }
    private static bool BlocksMutations(AdminJob job) => job.State == JobState.Uncertain &&
        (job.Action is LabAction.Install or LabAction.Cleanup or LabAction.RestartApplication || job.Action != LabAction.Wake && job.ExecutedOs != LabOs.Unknown);
    public async Task ReconcileAsync(AdminJob job, CancellationToken token)
    {
        if (_running.ContainsKey(job.Id)) throw new InvalidOperationException("Задание ещё выполняется.");
        var pc = Computers.FirstOrDefault(x => x.Id == job.ComputerId) ?? throw new InvalidOperationException("Компьютер не найден.");
        using var lease = await MachineMutationGate.AcquireComputerAsync(pc, token, reconciliation: true);
        var state = await ProbeAsync(pc, token); if (!state.Reachable) throw new InvalidOperationException("Управление недоступно.");
        if (job.Action is LabAction.BootWindows or LabAction.WakeWindows or LabAction.RestartAstra or LabAction.Wake)
        {
            var target = job.Action is LabAction.BootWindows or LabAction.WakeWindows ? LabOs.Windows : LabOs.Astra;
            if (state.Os == target && state.BootId != job.BootBefore) { SetJob(job, JobState.Succeeded, "Фактическая загрузка подтверждена при сверке."); return; }
        }
        if (job.Action is LabAction.Shutdown or LabAction.BootWindows or LabAction.WakeWindows or LabAction.RestartAstra && job.BootBefore.Length > 0 && state.BootId != job.BootBefore)
        {
            SetJob(job, JobState.Failed, "Подтверждена новая загрузка этого ПК, прежняя команда больше не выполняется. Исторический результат выключения или требуемой загрузки не подтверждён."); return;
        }
        if (state.Os != job.ExecutedOs) { SetJob(job, JobState.Uncertain, "Для чтения результата требуется ОС, в которой выполнялось задание."); return; }
        var result = await _adapters[state.Os].ReconcileAsync(Endpoint(pc, state), job.Id, token);
        if (result is null) SetJob(job, JobState.Uncertain, "Файл результата ещё отсутствует; повторный запуск не выполнялся."); else SetJob(job, result.Result.State, result.Detail);
    }
    public async Task StopAsync() { _lifetime.Cancel(); await Task.WhenAll(_running.Values.ToArray()); }
    public void Dispose() { _lifetime.Cancel(); }
}
