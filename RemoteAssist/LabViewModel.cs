using Microsoft.Win32;
using System.Security;
using System.Windows.Threading;

namespace RemoteAssist;

public sealed class ComputerCard : ObservableObject
{
    private bool _selected;
    public LabComputer Computer { get; private set; }
    public MachineSnapshot? Snapshot { get; private set; }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Name => Computer.Name;
    public string Room => Computer.Room;
    public string Seat => Computer.Seat.Length > 0 ? "Место " + Computer.Seat : "Место не задано";
    public string ActiveOs => Snapshot?.Reachable == true ? LabText.Os(Snapshot.Os) : "ОС не подтверждена";
    public string Status => Snapshot?.State ?? "Ещё не проверен";
    public string User => Snapshot?.User ?? "";
    public string Addresses => string.Join(", ", Computer.Systems.Select(x => x.Address).Distinct());
    public string Detail => Snapshot?.Detail ?? Computer.InventoryNote;
    public string Checked => Snapshot is null ? "Нет живой проверки" : "Проверен " + Snapshot.CheckedAt.ToLocalTime().ToString("dd.MM HH:mm:ss");
    public ComputerCard(LabComputer pc) { Computer = pc; }
    public void Update(LabComputer pc, MachineSnapshot? snapshot) { Computer = pc; Snapshot = snapshot; OnPropertyChanged(""); }
}

public sealed class LabViewModel : ObservableObject, IDisposable
{
    private readonly LabStore _store;
    private readonly DirectoryConfiguration _configuration;
    private readonly LabSecrets _secrets;
    private readonly LabRuntime _runtime;
    private readonly AstraLabAdapter? _astra;
    private readonly ISessionService _sessions;
    private readonly IRemoteDesktopLauncher _rdp;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource _cancel = new();
    private readonly CancellationTokenSource _background = new();
    private Task _backgroundWork = Task.CompletedTask;
    private readonly List<AstraDesktopSession> _desktops = [];
    private bool _busy, _ticking, _disposed, _refreshQueued;
    private Task _active = Task.CompletedTask;
    private string _message = "Импортируйте CSV GLPI, загрузите компьютеры из AD или добавьте вручную. API-токен необязателен.", _room = "Все аудитории", _query = "";
    private ComputerCard? _selected;
    private SoftwarePackage? _selectedPackage;
    private AllowedApplication? _selectedApplication;
    private SecureString? _glpiUser, _glpiApp, _sshSecret;
    private bool _removeGlpi, _removeSsh;
    private InventoryLinkService? _inventoryLinks;
    public LabRuntime Runtime => _runtime;
    public ObservableCollection<ComputerCard> Computers { get; } = [];
    public ICollectionView ComputersView { get; }
    public ObservableCollection<string> Rooms { get; } = ["Все аудитории"];
    public ObservableCollection<SoftwarePackage> Packages { get; } = [];
    public ObservableCollection<AllowedApplication> Applications { get; } = [];
    public ObservableCollection<StudentProfile> Profiles { get; } = [];
    public ObservableCollection<AdminJob> Jobs { get; } = [];
    public ObservableCollection<LabAlert> Alerts { get; } = [];
    public ObservableCollection<LabSchedule> Schedules { get; } = [];
    public ObservableCollection<LabProcess> Processes { get; } = [];
    public ObservableCollection<LabDisk> Disks { get; } = [];
    public ObservableCollection<LabService> Services { get; } = [];
    public string HardwareText { get; private set; } = "";
    public string DiagnosticsText { get; private set; } = "";
    public string OsHistoryText { get; private set; } = "";
    public LabOptions OptionsDraft { get; private set; }
    public LabComputer ComputerDraft { get; private set; } = new();
    public string WindowsAddress { get; set; } = "";
    public string AstraAddress { get; set; } = "";
    public string SshFingerprint { get; set; } = "";
    public string MacText { get; set; } = "";
    public SoftwarePackage PackageDraft { get; private set; } = new();
    public AllowedApplication ApplicationDraft { get; private set; } = new();
    public LabProcess? SelectedProcess { get; set; }
    public AdminJob? SelectedJob { get; set; }
    public LabSchedule? SelectedSchedule { get; set; }
    public DateTime ScheduleDate { get; set; } = DateTime.Today;
    public string ScheduleTime { get; set; } = "18:00";
    public bool ScheduleWeekly { get; set; }
    public LabAction ScheduleAction { get; set; } = LabAction.Shutdown;
    public string ScheduleName { get; set; } = "После занятий";
    public Array OsChoices { get; } = new[] { LabOs.Windows, LabOs.Astra };
    public string[] PackageKinds { get; } = ["MSI", "EXE", "DEB", "APT"];
    public string[] Architectures { get; } = ["x64", "x86", "arm64", "any"];
    public Dictionary<LabAction, string> PowerChoices { get; } = Enum.GetValues<LabAction>().Where(x => x is not (LabAction.Install or LabAction.Cleanup or LabAction.RestartApplication)).ToDictionary(x => x, LabText.Action);
    public string Room { get => _room; set { if (Set(ref _room, value)) ComputersView.Refresh(); } }
    public string Query { get => _query; set { if (Set(ref _query, value)) ComputersView.Refresh(); } }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); OnPropertyChanged(nameof(CanEdit)); } }
    public bool CanEdit => !Busy && !_runtime.IsBusy;
    public bool LinkedPlacementReadOnly => _inventoryLinks?.GetLink(ComputerDraft.Id) is not null;
    public string LinkedInventoryStatus => _inventoryLinks?.GetStatus(ComputerDraft.Id) ?? "";
    public string Summary => $"Компьютеров: {Computers.Count} · Управление доступно: {Computers.Count(x => x.Snapshot?.Reachable == true)} · Открытых проблем: {Alerts.Count(x => x.ResolvedAt is null)}";
    public ComputerCard? SelectedComputer { get => _selected; set { if (Set(ref _selected, value)) LoadComputer(); } }
    public SoftwarePackage? SelectedPackage { get => _selectedPackage; set { if (Set(ref _selectedPackage, value) && value is not null) { PackageDraft = JsonSerializer.Deserialize<SoftwarePackage>(JsonSerializer.Serialize(value))!; OnPropertyChanged(nameof(PackageDraft)); } } }
    public AllowedApplication? SelectedApplication { get => _selectedApplication; set { if (Set(ref _selectedApplication, value) && value is not null) { ApplicationDraft = JsonSerializer.Deserialize<AllowedApplication>(JsonSerializer.Serialize(value))!; OnPropertyChanged(nameof(ApplicationDraft)); } } }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ImportAdCommand { get; }
    public RelayCommand ImportCsvCommand { get; }
    public AsyncRelayCommand PollCommand { get; }
    public AsyncRelayCommand DiagnoseCommand { get; }
    public AsyncRelayCommand PreviewProfilesCommand { get; }
    public AsyncRelayCommand CleanupCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand RestartApplicationCommand { get; }
    public AsyncRelayCommand ReconcileCommand { get; }
    public RelayCommand PowerCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand NewComputerCommand { get; }
    public RelayCommand SaveComputerCommand { get; }
    public RelayCommand FillIdentityCommand { get; }
    public RelayCommand MergeCommand { get; }
    public RelayCommand MoveInInventoryCommand { get; }
    public RelayCommand SaveOptionsCommand { get; }
    public RelayCommand CancelOptionsCommand { get; }
    public RelayCommand ClearGlpiSecretsCommand { get; }
    public RelayCommand ClearSshSecretCommand { get; }
    public RelayCommand ChooseViewerCommand { get; }
    public RelayCommand ChooseKeyCommand { get; }
    public RelayCommand NewPackageCommand { get; }
    public RelayCommand ChoosePackageCommand { get; }
    public RelayCommand SavePackageCommand { get; }
    public RelayCommand DeletePackageCommand { get; }
    public RelayCommand NewApplicationCommand { get; }
    public RelayCommand SaveApplicationCommand { get; }
    public RelayCommand SaveScheduleCommand { get; }
    public RelayCommand DeleteScheduleCommand { get; }
    public RelayCommand CancelCommand { get; }
    public event Action? SecretsReset;
    public event Action<string>? MoveInInventoryRequested;
    public LabViewModel(LabStore store, DirectoryConfiguration configuration, LabSecrets secrets, LabRuntime runtime, ISessionService sessions, IRemoteDesktopLauncher rdp, AstraLabAdapter? astra = null)
    {
        _store = store; _configuration = configuration; _secrets = secrets; _runtime = runtime; _sessions = sessions; _rdp = rdp; _astra = astra;
        OptionsDraft = configuration.Settings.Lab.Copy();
        ComputersView = CollectionViewSource.GetDefaultView(Computers);
        ComputersView.Filter = x => x is ComputerCard c && (Room == "Все аудитории" || c.Room == Room) && (Query.Length == 0 || (c.Name + " " + c.Seat + " " + c.Addresses + " " + c.Computer.Serial).Contains(Query, StringComparison.OrdinalIgnoreCase));
        ComputersView.SortDescriptions.Add(new(nameof(ComputerCard.Room), ListSortDirection.Ascending));
        ComputersView.SortDescriptions.Add(new("Computer.Position", ListSortDirection.Ascending));
        foreach (var p in store.Load<SoftwarePackage>("package")) Packages.Add(p);
        foreach (var app in store.Load<AllowedApplication>("application")) Applications.Add(app);
        ImportCommand = Async(async token => { var source = new GlpiInventorySource(_configuration.Settings.Lab.Copy(), secrets); runtime.Import(await source.ReadAsync(token)); Message = "Инвентарь GLPI загружен. Выполните живую проверку."; });
        ImportAdCommand = Async(async token => { var directory = new ActiveDirectoryService(_configuration); var pcs = await directory.GetSearchComputersAsync(token); runtime.Import(pcs.Select(InventoryImport.FromAd)); Message = "Компьютеры загружены из AD. Укажите аудитории и подтвердите аппаратные ID через диагностику."; });
        ImportCsvCommand = new(_ => Guard(() => { var picker = new OpenFileDialog { Filter = "CSV GLPI|*.csv;*.tsv", Title = "Выгрузка списка компьютеров GLPI" }; if (picker.ShowDialog() != true) return; runtime.Import(InventoryImport.Csv(picker.FileName)); Message = "CSV GLPI импортирован без API-токена. Выполните проверку состояния."; }));
        PollCommand = Async(async token => { await runtime.PollAsync(token); Message = "Опрос завершён."; });
        DiagnoseCommand = Async(async token => { var pc = RequireComputer(); var state = await runtime.ProbeAsync(pc, token, true); Message = state.State + ": " + state.Detail; RefreshDetails(); });
        PreviewProfilesCommand = Async(async token =>
        {
            Profiles.Clear(); foreach (var pc in Targets()) { try { foreach (var p in await runtime.ProfilesAsync(pc, token)) Profiles.Add(p); } catch (OperationCanceledException) { throw; } catch (Exception ex) { Message = pc.Name + ": " + ex.Message; } }
            Message = $"Профилей: {Profiles.Count}. Доступно для удаления: {Profiles.Count(x => x.Eligible)}. Отметьте конкретные профили.";
        });
        CleanupCommand = Async(async token =>
        {
            var selected = Profiles.Where(x => x.Selected && x.Eligible).ToArray(); if (selected.Length == 0) throw new InvalidOperationException("Отметьте профили после предварительной проверки.");
            if (!Confirm("Удалить локальные профили и их файлы? Учётные записи сохранятся.\n\n" + string.Join("\n", selected.Take(20).Select(x => $"{x.Computer}: {x.Login} · {x.Path}")))) return;
            await Task.WhenAll(selected.GroupBy(x => x.ComputerId).Select(g => runtime.EnqueueAsync(runtime.Computers.Single(x => x.Id == g.Key), LabAction.Cleanup, new CleanupPayload(g.ToArray()), token)));
            Profiles.Clear(); Message = "Очистка завершена. Результаты — в заданиях; для следующего удаления выполните новый просмотр.";
        });
        InstallCommand = Async(async token => { var package = SelectedPackage ?? throw new InvalidOperationException("Выберите сохранённый пакет."); package.Validate(); var targets = Targets(); if (Confirm($"Установить «{package.Name} {package.Version}» на {targets.Length} ПК?")) await Task.WhenAll(targets.Select(pc => runtime.EnqueueAsync(pc, LabAction.Install, new PackagePayload(package), token))); });
        ConnectCommand = Async(ConnectAsync);
        DisconnectCommand = Async(async _ => { foreach (var session in _desktops) await session.DisposeAsync(); _desktops.Clear(); Message = "Сеансы помощи Astra закрыты."; });
        RestartApplicationCommand = Async(async token => { var pc = RequireComputer(); var app = SelectedApplication ?? throw new InvalidOperationException("Выберите приложение из сохранённого списка."); var process = SelectedProcess ?? throw new InvalidOperationException("Выберите процесс после диагностики."); await runtime.EnqueueAsync(pc, LabAction.RestartApplication, new RestartPayload(app, process), token); });
        ReconcileCommand = Async(async token => { if (SelectedJob is null) throw new InvalidOperationException("Выберите задание."); await runtime.ReconcileAsync(SelectedJob, token); });
        PowerCommand = new(async value => { if (Enum.TryParse<LabAction>(value?.ToString(), out var action)) await RunAsync(async token => { var pcs = Targets(); if (Confirm($"{LabText.Action(action)}: {pcs.Length} ПК. Занятым пользователям будет показано предупреждение на 120 секунд.\n\n" + string.Join(", ", pcs.Select(x => x.Name)))) await Task.WhenAll(pcs.Select(pc => runtime.EnqueueAsync(pc, action, null, token))); }); });
        SelectAllCommand = new(_ => { foreach (ComputerCard row in ComputersView) row.Selected = true; });
        SelectNoneCommand = new(_ => { foreach (var row in Computers) row.Selected = false; });
        NewComputerCommand = new(_ => { _selected = null; OnPropertyChanged(nameof(SelectedComputer)); ComputerDraft = new(); WindowsAddress = AstraAddress = MacText = SshFingerprint = ""; DraftChanged(); });
        SaveComputerCommand = new(_ => Guard(SaveComputer));
        FillIdentityCommand = new(_ => Guard(() =>
        {
            var snapshot = SelectedComputer?.Snapshot;
            if (snapshot is null || DateTimeOffset.UtcNow - snapshot.CheckedAt > TimeSpan.FromMinutes(2) || !MachineIdentity.Valid(snapshot.Uuid) && !MachineIdentity.Valid(snapshot.Serial)) throw new InvalidOperationException("Сначала выполните диагностику этого ПК. Нужен свежий ответ с аппаратными ID.");
            if (!Confirm($"Использовать аппаратные ID из последней проверки {SelectedComputer!.Name}?\nОС: {snapshot.Version}\nИмя в ответе: {snapshot.HostName}\nUUID: {snapshot.Uuid}\nСерийный номер: {snapshot.Serial}")) return;
            ComputerDraft.Uuid = MachineIdentity.Valid(snapshot.Uuid) ? snapshot.Uuid : ""; ComputerDraft.Serial = MachineIdentity.Valid(snapshot.Serial) ? snapshot.Serial : ""; ComputerDraft.BootPilotVerified = false; ComputerDraft.BootConfigHash = "";
            OnPropertyChanged(nameof(ComputerDraft)); Message = "Аппаратные ID заполнены в черновике. Проверьте и сохраните карточку.";
        }));
        MergeCommand = new(_ => Guard(Merge));
        MoveInInventoryCommand = new(_ =>
        {
            if (_inventoryLinks?.CanOpenInventory(ComputerDraft.Id) == true && _inventoryLinks.GetLink(ComputerDraft.Id) is { } link)
                MoveInInventoryRequested?.Invoke(link.AssetId);
        }, _ => _inventoryLinks?.CanOpenInventory(ComputerDraft.Id) == true);
        SaveOptionsCommand = new(_ => Guard(SaveOptions));
        CancelOptionsCommand = new(_ => ResetOptions());
        ClearGlpiSecretsCommand = new(_ => { _removeGlpi = true; Message = "Токены GLPI будут удалены после сохранения настроек."; });
        ClearSshSecretCommand = new(_ => { _removeSsh = true; Message = "Секрет SSH будет удалён после сохранения настроек."; });
        ChooseViewerCommand = new(_ => { var picker = new OpenFileDialog { Filter = "TigerVNC Viewer|vncviewer*.exe|Программы|*.exe" }; if (picker.ShowDialog() == true) { OptionsDraft.ViewerPath = picker.FileName; OnPropertyChanged(nameof(OptionsDraft)); } });
        ChooseKeyCommand = new(_ => { var picker = new OpenFileDialog { Title = "Закрытый ключ SSH" }; if (picker.ShowDialog() == true) { OptionsDraft.SshPrivateKey = picker.FileName; OnPropertyChanged(nameof(OptionsDraft)); } });
        NewPackageCommand = new(_ => { SelectedPackage = null; PackageDraft = new(); OnPropertyChanged(nameof(PackageDraft)); });
        ChoosePackageCommand = new(_ => Guard(() => { var picker = new OpenFileDialog { Filter = "Установщики|*.msi;*.exe;*.deb" }; if (picker.ShowDialog() != true) return; PackageDraft.Source = picker.FileName; PackageDraft.Kind = Path.GetExtension(picker.FileName).TrimStart('.').ToUpperInvariant(); PackageDraft.Os = PackageDraft.Kind == "DEB" ? LabOs.Astra : LabOs.Windows; PackageDraft.Sha256 = LabWire.Sha(picker.FileName); OnPropertyChanged(nameof(PackageDraft)); }));
        SavePackageCommand = new(_ => Guard(() => { PackageDraft.Validate(); var saved = JsonSerializer.Deserialize<SoftwarePackage>(JsonSerializer.Serialize(PackageDraft))!; _store.Save("package", saved.Id, saved); var old = Packages.FirstOrDefault(x => x.Id == saved.Id); if (old is not null) Packages.Remove(old); Packages.Add(saved); SelectedPackage = saved; Message = "Пакет сохранён."; }));
        DeletePackageCommand = new(_ => Guard(() => { if (SelectedPackage is null) return; _store.Delete("package", SelectedPackage.Id); Packages.Remove(SelectedPackage); SelectedPackage = null; }));
        NewApplicationCommand = new(_ => { SelectedApplication = null; ApplicationDraft = new(); OnPropertyChanged(nameof(ApplicationDraft)); });
        SaveApplicationCommand = new(_ => Guard(() => { if (ApplicationDraft.Name.Length == 0 || ApplicationDraft.Path.Length == 0 || ApplicationDraft.Os == LabOs.Unknown) throw new InvalidOperationException("Укажите имя, ОС и полный путь приложения."); var saved = JsonSerializer.Deserialize<AllowedApplication>(JsonSerializer.Serialize(ApplicationDraft))!; _store.Save("application", saved.Os + ":" + saved.Path, saved); var old = Applications.FirstOrDefault(x => x.Os == saved.Os && x.Path == saved.Path); if (old is not null) Applications.Remove(old); Applications.Add(saved); SelectedApplication = saved; Message = "Разрешённое приложение сохранено."; }));
        SaveScheduleCommand = new(_ => Guard(() => { var pcs = Targets(); if (!TimeSpan.TryParse(ScheduleTime, out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) throw new InvalidOperationException("Время в формате ЧЧ:ММ."); var date = new DateTimeOffset(ScheduleDate.Date + time); if (date <= DateTimeOffset.Now) throw new InvalidOperationException("Укажите будущее время запуска."); runtime.SaveSchedule(new() { Name = ScheduleName, Action = ScheduleAction, ComputerIds = pcs.Select(x => x.Id).ToList(), NextRun = date, Weekly = ScheduleWeekly }); Message = "Расписание сохранено. Оно работает только при открытом приложении."; }));
        DeleteScheduleCommand = new(_ => { if (SelectedSchedule is not null) runtime.RemoveSchedule(SelectedSchedule); });
        CancelCommand = new(_ => { _cancel.Cancel(); Message = "Новые действия остановлены. Начатые операции проверяются без принудительного прерывания установщика."; });
        runtime.Changed += QueueRefresh;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) => { if (!_ticking) { _backgroundWork = Tick(); await _backgroundWork; } }, _dispatcher); _timer.Stop();
        Refresh();
    }
    public void Start() => _timer.Start();
    public void AttachInventoryLinks(InventoryLinkService links)
    {
        if (_inventoryLinks is not null) _inventoryLinks.Changed -= QueueRefresh;
        _inventoryLinks = links; links.Changed += QueueRefresh;
        Refresh();
    }
    private async Task Tick()
    {
        if (_ticking || _disposed || _background.IsCancellationRequested) return;
        _ticking = true; try { await _runtime.TickAsync(_background.Token); } catch (OperationCanceledException) { } catch (Exception ex) { Message = ex.Message; } finally { _ticking = false; }
    }
    private AsyncRelayCommand Async(Func<CancellationToken, Task> action) => new(() => RunAsync(action), () => !Busy);
    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (Busy || _disposed) return Task.CompletedTask;
        _active = Core(); return _active;
        async Task Core()
        {
            _cancel.Dispose(); _cancel = new(); Busy = true;
            try { await action(_cancel.Token); }
            catch (OperationCanceledException) { Message = "Операция отменена."; }
            catch (Exception ex) { Message = LabRuntime.Error(ex); }
            finally { Busy = false; Refresh(); }
        }
    }
    private void Guard(Action action) { try { if (!CanEdit) throw new InvalidOperationException("Дождитесь завершения текущих заданий."); action(); } catch (Exception ex) { Message = ex.Message; } }
    private static bool Confirm(string message) => MessageBox.Show(message, "Remote Desk", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private LabComputer RequireComputer() => SelectedComputer?.Computer ?? throw new InvalidOperationException("Выберите карточку компьютера.");
    private LabComputer[] Targets() { var pcs = Computers.Where(x => x.Selected).Select(x => x.Computer).ToArray(); if (pcs.Length == 0) throw new InvalidOperationException("Отметьте компьютеры в разделе «Аудитории»."); return pcs; }
    private void QueueRefresh()
    {
        lock (_store) { if (_refreshQueued || _disposed) return; _refreshQueued = true; }
        _dispatcher.BeginInvoke(() => { lock (_store) _refreshQueued = false; if (!_disposed) Refresh(); });
    }
    public void Refresh()
    {
        foreach (var pc in _runtime.Computers.ToArray())
        {
            var row = Computers.FirstOrDefault(x => x.Computer.Id == pc.Id); if (row is null) { row = new(pc); Computers.Add(row); }
            _runtime.Snapshots.TryGetValue(pc.Id, out var state); row.Update(pc, state);
        }
        foreach (var row in Computers.Where(x => !_runtime.Computers.Any(p => p.Id == x.Computer.Id)).ToArray()) Computers.Remove(row);
        var rooms = new[] { "Все аудитории" }.Concat(Computers.Select(x => x.Room).Distinct().Order()).ToArray();
        if (!Rooms.SequenceEqual(rooms)) { Rooms.Clear(); foreach (var room in rooms) Rooms.Add(room); if (!Rooms.Contains(Room)) Room = Rooms[0]; }
        var jobId = SelectedJob?.Id; var scheduleId = SelectedSchedule?.Id;
        Replace(Jobs, _runtime.Jobs.OrderByDescending(x => x.CreatedAt).ToArray()); Replace(Alerts, _runtime.Alerts.OrderBy(x => x.ResolvedAt is not null).ThenByDescending(x => x.OpenedAt).ToArray()); Replace(Schedules, _runtime.Schedules.ToArray());
        SelectedJob = Jobs.FirstOrDefault(x => x.Id == jobId); SelectedSchedule = Schedules.FirstOrDefault(x => x.Id == scheduleId);
        OnPropertyChanged(nameof(SelectedJob)); OnPropertyChanged(nameof(SelectedSchedule));
        ComputersView.Refresh(); RefreshDetails(); RefreshInventoryPlacement(); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(CanEdit));
    }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items) { collection.Clear(); foreach (var item in items) collection.Add(item); }
    private void LoadComputer()
    {
        SelectedProcess = null;
        if (SelectedComputer is not null)
        {
            ComputerDraft = SelectedComputer.Computer.Copy(); WindowsAddress = ComputerDraft.Systems.FirstOrDefault(x => x.Os == LabOs.Windows)?.Address ?? ComputerDraft.Systems.FirstOrDefault()?.Address ?? "";
            AstraAddress = ComputerDraft.Systems.FirstOrDefault(x => x.Os == LabOs.Astra)?.Address ?? WindowsAddress;
            SshFingerprint = ComputerDraft.Systems.FirstOrDefault(x => x.Os == LabOs.Astra)?.SshFingerprint ?? ""; MacText = string.Join("\n", ComputerDraft.MacAddresses); DraftChanged();
        }
        RefreshDetails();
    }
    private void DraftChanged() { foreach (var p in new[] { nameof(ComputerDraft), nameof(WindowsAddress), nameof(AstraAddress), nameof(SshFingerprint), nameof(MacText) }) OnPropertyChanged(p); RefreshInventoryPlacement(); }
    private void RefreshInventoryPlacement()
    {
        if (LinkedPlacementReadOnly && _runtime.FindComputer(ComputerDraft.Id) is { } latest)
        {
            ComputerDraft.Room = latest.Room; ComputerDraft.Seat = latest.Seat; ComputerDraft.LocalPlacement = true;
            OnPropertyChanged(nameof(ComputerDraft));
        }
        OnPropertyChanged(nameof(LinkedPlacementReadOnly)); OnPropertyChanged(nameof(LinkedInventoryStatus));
        MoveInInventoryCommand.RaiseCanExecuteChanged();
    }
    private void RefreshDetails()
    {
        var snapshot = SelectedComputer?.Snapshot;
        var process = SelectedProcess;
        Replace(Processes, snapshot?.Processes ?? []); Replace(Disks, snapshot?.Disks ?? []); Replace(Services, snapshot?.Services ?? []);
        SelectedProcess = Processes.FirstOrDefault(x => x == process); OnPropertyChanged(nameof(SelectedProcess));
        HardwareText = snapshot is null ? "Выполните диагностику выбранного ПК." : string.Join("\n", snapshot.Hardware.Select(x => x.Key + ": " + x.Value));
        DiagnosticsText = snapshot is null ? "" : string.Join("\n", snapshot.Diagnostics.Select(x => x.Key + ": " + x.Value));
        OsHistoryText = SelectedComputer is null ? "" : string.Join("\n", SelectedComputer.Computer.Systems.Select(x => $"{LabText.Os(x.Os)} · {x.Address} · {x.LastSnapshot?.Version ?? x.Description} · Последняя проверка: {x.LastSnapshot?.CheckedAt.ToLocalTime().ToString("g") ?? "нет"} · Инвентарь GLPI: {x.InventoryAt?.ToLocalTime().ToString("g") ?? "дата не предоставлена"}"));
        OnPropertyChanged(nameof(HardwareText)); OnPropertyChanged(nameof(DiagnosticsText)); OnPropertyChanged(nameof(OsHistoryText));
    }
    private void SaveComputer()
    {
        _inventoryLinks?.PreserveLinkedPlacement(ComputerDraft);
        if (string.IsNullOrWhiteSpace(ComputerDraft.Name) || string.IsNullOrWhiteSpace(ComputerDraft.Room)) throw new InvalidOperationException("Укажите имя и аудиторию.");
        foreach (var address in new[] { WindowsAddress, AstraAddress }.Where(x => x.Length > 0)) LabWire.ValidateHost(address);
        if (WindowsAddress.Length == 0 && AstraAddress.Length == 0) throw new InvalidOperationException("Укажите хотя бы один адрес.");
        if (SshFingerprint.Length > 0 && !Regex.IsMatch(SshFingerprint, "^SHA256:[A-Za-z0-9+/]{43}$")) throw new InvalidOperationException("SSH: нужен отпечаток SHA256, сверенный на компьютере.");
        var copy = ComputerDraft.Copy(); copy.MacAddresses = LabOptions.Lines(MacText).ToList(); foreach (var mac in copy.MacAddresses) WakeOnLan.Packet(mac);
        foreach (var (os, address) in new[] { (LabOs.Windows, WindowsAddress), (LabOs.Astra, AstraAddress) })
        {
            if (address.Length == 0) continue;
            var endpoint = copy.Systems.FirstOrDefault(x => x.Os == os && x.Address == address); if (endpoint is null) { endpoint = new() { Os = os, Address = address }; copy.Systems.Add(endpoint); }
            if (os == LabOs.Astra) endpoint.SshFingerprint = SshFingerprint;
        }
        if (copy.BootPilotVerified)
        {
            var snapshot = SelectedComputer?.Snapshot;
            if (snapshot?.Reachable != true || snapshot.Os != LabOs.Astra || !snapshot.Diagnostics.TryGetValue("GrubReady", out var ready) || ready != "yes" || !snapshot.Diagnostics.TryGetValue("WindowsMenuIds", out var ids) || !ids.Split('\n').Contains(copy.WindowsMenuId)) throw new InvalidOperationException("Для отметки испытания сначала выполните диагностику Astra и укажите обнаруженный ID Windows.");
            copy.BootConfigHash = snapshot.Diagnostics.GetValueOrDefault("BootConfigHash", "");
        }
        copy.LocalPlacement = true;
        if (_inventoryLinks is not null) _inventoryLinks.SaveComputer(copy); else _runtime.SaveComputer(copy);
        Refresh(); SelectedComputer = Computers.Single(x => x.Computer.Id == copy.Id); Message = "Карточка сохранена.";
    }
    private void Merge()
    {
        var pcs = Targets(); if (pcs.Length != 2) throw new InvalidOperationException("Для объединения отметьте ровно две записи одного физического ПК.");
        if (!Confirm("Объединить эти записи в один физический компьютер? Проверьте UUID и серийный номер.\n" + string.Join("\n", pcs.Select(x => $"{x.Name}: {x.Uuid}; {x.Serial}")))) return;
        if (_inventoryLinks is not null) _inventoryLinks.MergeComputers(pcs[0].Id, pcs[1].Id);
        else _runtime.MergeComputers(pcs[0].Id, pcs[1].Id);
        Refresh(); SelectedComputer = Computers.Single(x => x.Computer.Id == pcs[0].Id); Message = "Записи объединены.";
    }
    public void SetSecret(string kind, SecureString value)
    {
        switch (kind) { case "glpi-user": _glpiUser?.Dispose(); _glpiUser = value.Copy(); _removeGlpi = false; break; case "glpi-app": _glpiApp?.Dispose(); _glpiApp = value.Copy(); _removeGlpi = false; break; case "ssh": _sshSecret?.Dispose(); _sshSecret = value.Copy(); _removeSsh = false; break; }
    }
    private void SaveOptions()
    {
        OptionsDraft.Validate(); var candidate = _configuration.Settings.Copy(); candidate.Lab = OptionsDraft.Copy();
        _configuration.Save(candidate, null, false);
        if (_removeGlpi) { _secrets.Delete("glpi-user", OptionsDraft.GlpiUrl); _secrets.Delete("glpi-app", OptionsDraft.GlpiUrl); }
        else { if (_glpiUser is not null) _secrets.Save("glpi-user", OptionsDraft.GlpiUrl, _glpiUser); if (_glpiApp is not null) _secrets.Save("glpi-app", OptionsDraft.GlpiUrl, _glpiApp); }
        if (_removeSsh) _secrets.Delete("ssh", OptionsDraft.SshUser); else if (_sshSecret is not null) _secrets.Save("ssh", OptionsDraft.SshUser, _sshSecret);
        ResetOptions(); Message = "Настройки классов сохранены. Секреты хранятся в Windows Credential Manager.";
    }
    private void ResetOptions() { OptionsDraft = _configuration.Settings.Lab.Copy(); _glpiUser?.Dispose(); _glpiApp?.Dispose(); _sshSecret?.Dispose(); _glpiUser = _glpiApp = _sshSecret = null; _removeGlpi = _removeSsh = false; OnPropertyChanged(nameof(OptionsDraft)); SecretsReset?.Invoke(); }
    private async Task ConnectAsync(CancellationToken token)
    {
        var pc = RequireComputer(); var snapshot = await _runtime.ProbeAsync(pc, token); if (!snapshot.Reachable) throw new InvalidOperationException("Идентичность ПК и доступность управления не подтверждены.");
        if (snapshot.Os == LabOs.Windows)
        {
            var sessions = (await _sessions.GetSessionsAsync(snapshot.Address, token, true)).Where(x => x.CanConnect).ToArray();
            if (sessions.Length != 1) throw new InvalidOperationException("Найдено несколько сеансов или нет активного. Выберите нужный сеанс через раздел «Главная».");
            var expected = sessions[0]; var actual = (await _sessions.GetSessionsAsync(snapshot.Address, token, true)).SingleOrDefault(x => x.CanConnect && x.Id == expected.Id && x.Domain == expected.Domain && x.UserName == expected.UserName);
            if (actual is null) throw new InvalidOperationException("Сеанс изменился."); _rdp.Launch(actual); Message = "Запущено подключение с запросом согласия.";
        }
        else
        {
            if (_astra is null) throw new InvalidOperationException("Адаптер Astra не подключён.");
            var session = await AstraDesktopSession.StartAsync(_astra, _configuration.Settings.Lab.Copy(), pc, _runtime.Endpoint(pc, snapshot), token); _desktops.Add(session); Message = "TigerVNC запущен; ожидается согласие пользователя Astra.";
        }
    }
    public async Task StopAsync()
    {
        _timer.Stop(); _background.Cancel(); _cancel.Cancel(); await _active; await _backgroundWork; await _runtime.StopAsync(); foreach (var session in _desktops) await session.DisposeAsync(); _desktops.Clear();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); _background.Cancel(); _cancel.Cancel(); _runtime.Changed -= QueueRefresh; if (_inventoryLinks is not null) _inventoryLinks.Changed -= QueueRefresh; _runtime.Dispose(); _glpiUser?.Dispose(); _glpiApp?.Dispose(); _sshSecret?.Dispose(); }
}
