using Microsoft.Win32;
using RemoteAssist.Inventory;
using RemoteAssist.Inventory.Excel;
using System.Net;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RemoteAssist;

public sealed record InventoryAssetRow(AssetDto Value, string RoomText)
{
    public string Id => Value.Id;
    public string Title => $"{Value.Type} {Value.Model}".Trim();
    public string Placement => Value.X.HasValue ? "На схеме" : "Не расставлено";
    public string Summary => $"{Title} · {RoomText} · инв. {Value.InventoryNumber} · {Value.Id[..Math.Min(8, Value.Id.Length)]}";
}
public sealed record InventoryImportAction(SpreadsheetRowAction Value, string Label);
public sealed record InventoryPhotoRow(PhotoDto Value, BitmapSource? Image, string? Path);

public sealed class InventoryViewModel : ObservableObject, IDisposable
{
    private readonly InventoryClient _client;
    private readonly LabRuntime? _runtime;
    private readonly InventoryLinkService? _links;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly List<AsyncRelayCommand> _commands = [];
    private bool _busy, _rebuilding, _allRooms = true, _archived, _disposed;
    private string _snapshotScope = "";
    private int _floor = 1, _tab, _photoGeneration;
    private string _message = "Укажите адрес службы TrueNAS и войдите в разделе «Подключение».", _search = "", _password = "", _photoScope = "Оборудование";
    private RoomDto? _room;
    private InventoryAssetRow? _selectedAsset;
    private CartridgeDto? _selectedCartridge;
    private AuditDto? _audit;
    private AuditItemDto? _auditItem;
    private InventoryPhotoRow? _photo;
    public InventorySnapshot Snapshot => _client.Snapshot;
    public bool Online => _client.Online;
    public bool CanEdit => Online && !IsBusy;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); NotifyAvailability(); } }
    public string Message { get => _message; set => Set(ref _message, value); }
    public string ConnectionStatus => Online ? $"Подключено · ревизия {Snapshot.Revision} · {Snapshot.Assets.Count(x => !x.Archived)} объектов" : Snapshot.RetrievedUtc == default ? "Нет подключения" : $"Просмотр копии от {Snapshot.RetrievedUtc.LocalDateTime:g} · сервер недоступен";
    public string ServerUrl { get; set; }
    public string Username { get; set; }
    public int[] Floors { get; } = [1, 2, 3, 4, 5];
    public int Floor { get => _floor; set { if (Set(ref _floor, value)) { Rebuild(); SelectedRoom = Rooms.FirstOrDefault(x => x.Floor == value); } } }
    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) Rebuild(); } }
    public bool AllRooms { get => _allRooms; set { if (Set(ref _allRooms, value)) Rebuild(); } }
    public bool IncludeArchived { get => _archived; set { if (Set(ref _archived, value)) Rebuild(); } }
    public ObservableCollection<RoomDto> Rooms { get; } = [];
    public ObservableCollection<RoomDto> FloorRooms { get; } = [];
    public ObservableCollection<InventoryAssetRow> Assets { get; } = [];
    public ObservableCollection<InventoryAssetRow> RoomAssets { get; } = [];
    public ObservableCollection<InventoryAssetRow> UnplacedAssets { get; } = [];
    public ObservableCollection<AssetDto> Printers { get; } = [];
    public ObservableCollection<CartridgeDto> Cartridges { get; } = [];
    public ObservableCollection<CartridgeDto> FilteredCartridges { get; } = [];
    public ObservableCollection<CartridgeDto> SpareCartridges { get; } = [];
    public string[] CartridgeGroups { get; } = ["Все", "Запас", "Установлены", "На заправке", "Списанные"];
    private string _cartridgeGroup = "Все";
    public string CartridgeGroup { get => _cartridgeGroup; set { if (Set(ref _cartridgeGroup, value)) Rebuild(); } }
    public string CartridgeGroupSummary => $"В группе: {FilteredCartridges.Count} · всего: {Cartridges.Count}";
    public IReadOnlyList<InventoryAssetRow> LinkTargets { get; private set; } = [];
    private string? _linkSourceId;
    public bool IsLinking => _linkSourceId is not null;
    public string SelectionHint => IsLinking ? "Теперь нажмите основное устройство. Esc — отменить связь." : "Двойной щелчок — карточка · перетаскивание — расстановка";
    public event Action? AssetCardRequested;
    public RelayCommand OpenAssetCommand { get; }
    public RelayCommand BeginLinkCommand { get; }
    public AsyncRelayCommand UnlinkAssetCommand { get; }
    public AsyncRelayCommand OpenCorridorCommand { get; }
    public ObservableCollection<AuditDto> Audits { get; } = [];
    public ObservableCollection<AuditItemDto> AuditItems { get; } = [];
    public ObservableCollection<HistoryDto> History { get; } = [];
    public ObservableCollection<InventoryPhotoRow> Photos { get; } = [];
    public ObservableCollection<UserDto> Users { get; } = [];
    public ObservableCollection<LabComputer> LocalComputers { get; } = [];
    public string[] AssetTypes { get; } = ["Системный блок", "Монитор", "Ноутбук", "Моноблок", "Принтер", "МФУ", "Проектор", "Телевизор", "Телефон", "Коммутатор", "Веб-камера", "Другое"];
    public string[] AssetStates { get; } = ["Исправно", "Неисправно", "На ремонте", "На хранении", "К списанию"];
    public string[] CartridgeStates { get; } = ["Запас", "На заправке", "Списан"];
    public string[] AuditResults { get; } = ["Не проверено", "Найдено", "Отсутствует", "Обнаружено в другом кабинете"];
    public string[] PhotoScopes { get; } = ["Оборудование", "Кабинет", "Проверка"];
    public string[] Scopes { get; } = ["Корпус", "Этаж", "Кабинет"];
    public string[] UserRoles { get; } = ["editor", "owner"];
    public RoomDto? SelectedRoom
    {
        get => _room;
        set { if (!Set(ref _room, value) || _rebuilding) return; CancelLink(); DraftRoom = Copy(value ?? new()); OnPropertyChanged(nameof(DraftRoom)); OnPropertyChanged(nameof(RoomTitle)); OnPropertyChanged(nameof(SelectedRoomNumber)); Rebuild(); _ = LoadPhotosAsync(); }
    }
    public string SelectedRoomNumber => SelectedRoom?.Number ?? "";
    public string RoomTitle => SelectedRoom is null ? "Выберите помещение" : SelectedRoom.Kind == "corridor" ? $"Коридор · {SelectedRoom.Floor} этаж · корпус {SelectedRoom.Building}" : $"{SelectedRoom.Number}{SelectedRoom.Building}  {SelectedRoom.Name}".Trim();
    public RoomDto DraftRoom { get; private set; } = new();
    public AssetDto DraftAsset { get; private set; } = new();
    public string CompatibleModels { get; set; } = "";
    public InventoryAssetRow? SelectedAsset
    {
        get => _selectedAsset;
        set { if (!Set(ref _selectedAsset, value) || _rebuilding) return; if (value is not null) SetAssetDraft(value.Value); RefreshHistory(); _ = LoadPhotosAsync(); }
    }
    public CartridgeDto DraftCartridge { get; private set; } = new();
    public CartridgeDto? SelectedCartridge
    {
        get => _selectedCartridge;
        set { if (!Set(ref _selectedCartridge, value) || _rebuilding) return; DraftCartridge = Copy(value ?? new()); OnPropertyChanged(nameof(DraftCartridge)); RefreshHistory(); }
    }
    private AssetDto? _selectedPrinter;
    public AssetDto? SelectedPrinter { get => _selectedPrinter; set { if (Set(ref _selectedPrinter, value)) OnPropertyChanged(nameof(InstalledCartridges)); } }
    public string InstalledCartridges => SelectedPrinter is null ? "Выберите принтер, чтобы увидеть установленные картриджи." : string.Join("\n", Snapshot.Cartridges.Where(x => x.PrinterId == SelectedPrinter.Id).Select(x => $"{x.Slot}: № {x.Number} · {x.Model} · {x.Color}").DefaultIfEmpty("В этом принтере картриджи не установлены."));
    public CartridgeDto? ReplacementCartridge { get; set; }
    public string CartridgeSlot { get; set; } = "Чёрный";
    public string RemovedCartridgeStatus { get; set; } = "На заправке";
    public string AuditName { get; set; } = "Инвентаризация";
    public DateTime AuditDate { get; set; } = DateTime.Today;
    public string AuditScope { get; set; } = "Кабинет";
    public AuditDto? SelectedAudit
    {
        get => _audit;
        set { if (!Set(ref _audit, value) || _rebuilding) return; Replace(AuditItems, value?.Items ?? []); SelectedAuditItem = AuditItems.FirstOrDefault(); OnPropertyChanged(nameof(AuditOpen)); }
    }
    public AuditItemDto? SelectedAuditItem
    {
        get => _auditItem;
        set { if (!Set(ref _auditItem, value) || _rebuilding) return; DraftAuditItem = Copy(value ?? new()); OnPropertyChanged(nameof(DraftAuditItem)); _ = LoadPhotosAsync(); }
    }
    public AuditItemDto DraftAuditItem { get; private set; } = new();
    public bool AuditOpen => SelectedAudit is { CompletedUtc: null };
    public string PhotoScope { get => _photoScope; set { if (Set(ref _photoScope, value)) _ = LoadPhotosAsync(); } }
    public InventoryPhotoRow? SelectedPhoto { get => _photo; set { if (Set(ref _photo, value)) { PhotoCaption = value?.Value.Caption ?? ""; OnPropertyChanged(nameof(PhotoCaption)); } } }
    public string PhotoCaption { get; set; } = "";
    private LabComputer? _selectedComputer;
    public LabComputer? SelectedComputer { get => _selectedComputer; set { if (Set(ref _selectedComputer, value)) OnPropertyChanged(nameof(LinkSuggestions)); } }
    public string LinkSuggestions => SelectedComputer is null || _links is null ? "Связь создаётся только по вашему подтверждению." : "Совпадения по серийному номеру: " + string.Join("; ", _links.SuggestedAssets(SelectedComputer.Id).Select(x => Snapshot.Assets.FirstOrDefault(a => a.Id == x.AssetId)).Where(x => x is not null).Select(x => $"{x!.Type} {x.Model} · инв. {x.InventoryNumber} · {RoomText(x.RoomId)}").DefaultIfEmpty("не найдено"));
    private HistoryDto? _selectedHistory;
    public HistoryDto? SelectedHistory { get => _selectedHistory; set { if (Set(ref _selectedHistory, value)) OnPropertyChanged(nameof(HistoryDetails)); } }
    public string HistoryDetails => SelectedHistory is null ? "Выберите событие для просмотра сохранённых изменений." : $"До изменения:\n{SelectedHistory.BeforeJson}\n\nПосле изменения:\n{SelectedHistory.AfterJson}";
    public UserDto DraftUser { get; private set; } = new();
    public UserDto? SelectedUser { get; set; }
    public SpreadsheetImportOptions ImportOptions { get; } = new();
    public SpreadsheetPreview? ImportPreview { get; private set; }
    public string ImportPath { get; private set; } = "";
    public string ImportSummary => ImportPreview is null ? "Выберите XLSX. По умолчанию первая строка содержит данные." : $"Файл: {Path.GetFileName(ImportPath)}. Проверьте действия, замечания и кабинеты перед импортом.";
    public InventoryImportAction[] ImportActions { get; } = [new(SpreadsheetRowAction.Add, "Добавить"), new(SpreadsheetRowAction.Update, "Обновить"), new(SpreadsheetRowAction.Skip, "Пропустить")];
    public IReadOnlyList<InventoryAssetRow> ImportTargets { get; private set; } = [];
    public AsyncRelayCommand LoginCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SeedRoomsCommand { get; }
    public RelayCommand NewRoomCommand { get; }
    public AsyncRelayCommand SaveRoomCommand { get; }
    public RelayCommand NewAssetCommand { get; }
    public AsyncRelayCommand SaveAssetCommand { get; }
    public AsyncRelayCommand ArchiveAssetCommand { get; }
    public RelayCommand ReloadDraftCommand { get; }
    public AsyncRelayCommand ClearPlacementCommand { get; }
    public RelayCommand NewCartridgeCommand { get; }
    public AsyncRelayCommand SaveCartridgeCommand { get; }
    public AsyncRelayCommand ReplaceCartridgeCommand { get; }
    public AsyncRelayCommand RemoveCartridgeCommand { get; }
    public AsyncRelayCommand CreateAuditCommand { get; }
    public AsyncRelayCommand SaveAuditItemCommand { get; }
    public AsyncRelayCommand CompleteAuditCommand { get; }
    public AsyncRelayCommand AuditAddAssetCommand { get; }
    public AsyncRelayCommand ApplyAuditMoveCommand { get; }
    public AsyncRelayCommand UploadPhotoCommand { get; }
    public AsyncRelayCommand SavePhotoCommand { get; }
    public AsyncRelayCommand PrimaryPhotoCommand { get; }
    public RelayCommand ViewPhotoCommand { get; }
    public AsyncRelayCommand ReadImportCommand { get; }
    public AsyncRelayCommand ReReadImportCommand { get; }
    public AsyncRelayCommand ApplyImportCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ExportAuditCommand { get; }
    public RelayCommand LinkComputerCommand { get; }
    public RelayCommand UnlinkComputerCommand { get; }
    public AsyncRelayCommand RefreshUsersCommand { get; }
    public AsyncRelayCommand SaveUserCommand { get; }
    public RelayCommand NewUserCommand { get; }
    public RelayCommand EditUserCommand { get; }
    private string _userPassword = "";
    public event Action<InventoryPhotoRow>? ViewPhotoRequested;
    public event Action? LayoutChanged;

    public InventoryViewModel(InventoryClient client, LabRuntime? runtime = null, InventoryLinkService? links = null)
    {
        _client = client; _runtime = runtime; _links = links; ServerUrl = client.Connection.Url; Username = client.Connection.Username;
        OpenAssetCommand = new(_ => OpenAssetCard(DraftAsset.Id));
        BeginLinkCommand = new(_ => { if (DraftAsset.Version == 0) { Message = "Выберите сохранённую технику на схеме."; return; } _linkSourceId = DraftAsset.Id; OnPropertyChanged(nameof(SelectionHint)); Message = "Выбран компонент. Нажмите устройство, к которому его подключить."; }, _ => CanEdit);
        UnlinkAssetCommand = Command(async () => { var asset = Copy(Snapshot.Assets.Single(x => x.Id == DraftAsset.Id)); asset.ParentAssetId = null; await _client.SaveAsync("assets", asset); SelectAsset(asset.Id); Message = "Связь снята."; });
        OpenCorridorCommand = Command(OpenCorridorAsync, false);
        LoginCommand = Command(async () => { var password = _password; _password = ""; await _client.LoginAsync(ServerUrl, Username, password); Message = "Вход выполнен. Можно заполнить справочник кабинетов из планов."; }, false);
        LogoutCommand = Command(async () => { await _client.LogoutAsync(); Message = "Вы вышли. Доступна сохранённая копия."; }, false);
        RefreshCommand = Command(async () => { await _client.RefreshAsync(); Message = Online ? "Данные обновлены. Открытые черновики сохранены." : "Войдите в службу инвентаризации."; }, false);
        SeedRoomsCommand = Command(async () => { await _client.PostAsync("api/rooms/seed", new SeedRoomsCommand { Rooms = CorpusMaps.Floors.SelectMany(f => f.Rooms.Select(r => new RoomDto { Building = "Д", Floor = f.Floor, Number = r.Number, MapKey = $"d-{f.Floor}-{r.Number}", Name = r.Number == "101" ? "Актовый зал" : "" })).ToList() }); Message = "Кабинеты пяти этажей добавлены. Существующие карточки сохранены."; });
        NewRoomCommand = new(_ => { DraftRoom = new() { Floor = Floor }; OnPropertyChanged(nameof(DraftRoom)); });
        SaveRoomCommand = Command(async () => { var room = Copy(DraftRoom); await _client.SaveAsync("rooms", room); SelectedRoom = Rooms.FirstOrDefault(x => x.Id == room.Id); DraftRoom = Copy(SelectedRoom!); OnPropertyChanged(nameof(DraftRoom)); Message = "Кабинет сохранён."; });
        NewAssetCommand = new(_ => { SelectedAsset = null; SetAssetDraft(new() { RoomId = SelectedRoom?.Id }); Message = "Новая карточка: заполните реквизиты и сохраните."; AssetCardRequested?.Invoke(); }, _ => CanEdit);
        SaveAssetCommand = Command(SaveAssetAsync);
        ArchiveAssetCommand = Command(async () => { var asset = Copy(DraftAsset); if (asset.Version == 0) throw new InvalidOperationException("Сначала сохраните карточку."); asset.Archived = !asset.Archived; await _client.SaveAsync("assets", asset); SetAssetDraft(Snapshot.Assets.Single(x => x.Id == asset.Id)); Message = asset.Archived ? "Оборудование архивировано, история сохранена." : "Оборудование возвращено из архива."; });
        ReloadDraftCommand = new(_ => { var current = Snapshot.Assets.FirstOrDefault(x => x.Id == DraftAsset.Id); if (current is not null) SetAssetDraft(current); Message = "Черновик заменён текущими данными сервера."; });
        ClearPlacementCommand = Command(async () => { var asset = Copy(DraftAsset); asset.X = asset.Y = null; await _client.SaveAsync("assets", asset); SelectAsset(asset.Id); });
        NewCartridgeCommand = new(_ => { SelectedCartridge = null; DraftCartridge = new(); OnPropertyChanged(nameof(DraftCartridge)); });
        SaveCartridgeCommand = Command(async () => { var value = Copy(DraftCartridge); await _client.SaveAsync("cartridges", value); SelectedCartridge = Cartridges.Single(x => x.Id == value.Id); DraftCartridge = Copy(SelectedCartridge); OnPropertyChanged(nameof(DraftCartridge)); Message = "Картридж сохранён."; });
        ReplaceCartridgeCommand = Command(() => ChangeCartridgeAsync(false)); RemoveCartridgeCommand = Command(() => ChangeCartridgeAsync(true));
        CreateAuditCommand = Command(CreateAuditAsync);
        SaveAuditItemCommand = Command(async () => { var audit = RequireAudit(); await _client.PostAsync("api/audits/items", new AuditItemCommand { AuditId = audit.Id, AuditVersion = audit.Version, Value = Copy(DraftAuditItem) }); SelectAudit(audit.Id, DraftAuditItem.Id); Message = "Результат проверки сохранён."; });
        CompleteAuditCommand = Command(async () => { var audit = RequireAudit(); if (MessageBox.Show("Завершить проверку? Её результаты станут недоступны для редактирования.", "Инвентаризация", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return; await _client.PostAsync("api/audits/complete", new AuditCompleteCommand { AuditId = audit.Id, Version = audit.Version }); SelectAudit(audit.Id); Message = "Проверка завершена. Результат зафиксирован."; });
        AuditAddAssetCommand = Command(async () => { var audit = RequireAudit(); if (DraftAsset.Version == 0) throw new InvalidOperationException("Сначала сохраните найденное оборудование во вкладке «Техника»."); await _client.PostAsync("api/audits/add-asset", new AuditAddAssetCommand { AuditId = audit.Id, Version = audit.Version, AssetId = DraftAsset.Id }); SelectAudit(audit.Id); });
        ApplyAuditMoveCommand = Command(ApplyAuditMoveAsync);
        UploadPhotoCommand = Command(UploadPhotoAsync);
        SavePhotoCommand = Command(async () => { var value = Copy(SelectedPhoto?.Value ?? throw new InvalidOperationException("Выберите фотографию.")); value.Caption = PhotoCaption; await _client.SaveAsync("photos", value); await LoadPhotosAsync(); });
        PrimaryPhotoCommand = Command(async () => { var value = Copy(SelectedPhoto?.Value ?? throw new InvalidOperationException("Выберите фотографию.")); value.IsPrimary = true; await _client.SaveAsync("photos", value); await LoadPhotosAsync(); });
        ViewPhotoCommand = new(_ => { if (SelectedPhoto is not null) ViewPhotoRequested?.Invoke(SelectedPhoto); });
        ReadImportCommand = Command(async () => { var d = new OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx" }; if (d.ShowDialog() == true) { ImportPath = d.FileName; await ReadImportAsync(); } }, false);
        ReReadImportCommand = Command(ReadImportAsync, false);
        ApplyImportCommand = Command(async () => { if (ImportPreview is null) return; var command = InventorySpreadsheet.BuildImportCommand(ImportPreview, Snapshot); await _client.PostAsync("api/import", command); await ReadImportAsync(); Message = "Импорт применён. Повторные строки отмечены для пропуска."; });
        ExportCommand = Command(async () => { var d = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "Инвентаризация-" + DateTime.Today.ToString("yyyy-MM-dd") + ".xlsx" }; if (d.ShowDialog() == true) { var assets = Assets.Select(x => Copy(x.Value)).ToArray(); var snapshot = Copy(Snapshot); await Task.Run(() => InventorySpreadsheet.ExportRegistry(d.FileName, assets, snapshot)); Message = "Ведомость сохранена: " + d.FileName + (Online ? "" : " (из локальной копии)"); } }, false);
        ExportAuditCommand = Command(async () => { var audit = Copy(SelectedAudit ?? throw new InvalidOperationException("Выберите проверку.")); var d = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "Проверка-" + audit.Date.ToString("yyyy-MM-dd") + ".xlsx" }; if (d.ShowDialog() == true) { var snapshot = Copy(Snapshot); await Task.Run(() => InventorySpreadsheet.ExportAudit(d.FileName, audit, snapshot)); Message = "Результаты проверки сохранены."; } }, false);
        LinkComputerCommand = new(_ => Guard(() => { if (_links is null || SelectedComputer is null || DraftAsset.Version == 0) throw new InvalidOperationException("Выберите локальный компьютер и сохранённую карточку техники."); _links.Link(SelectedComputer.Id, DraftAsset.Id); Message = "Компьютер связан с общей карточкой."; }), _ => CanEdit);
        UnlinkComputerCommand = new(_ => Guard(() => { if (_links is not null && SelectedComputer is not null) _links.Unlink(SelectedComputer.Id); Message = "Локальная связь снята."; }), _ => CanEdit);
        RefreshUsersCommand = Command(async () => Replace(Users, await _client.UsersAsync()));
        NewUserCommand = new(_ => { DraftUser = new(); OnPropertyChanged(nameof(DraftUser)); });
        EditUserCommand = new(_ => { if (SelectedUser is not null) { DraftUser = Copy(SelectedUser); OnPropertyChanged(nameof(DraftUser)); } });
        SaveUserCommand = Command(async () => { var password = _userPassword; _userPassword = ""; var userId = DraftUser.Id; await _client.PostAsync("api/users", new SaveUserCommand { Value = Copy(DraftUser), Password = password.Length == 0 ? null : password }); Replace(Users, await _client.UsersAsync()); DraftUser = Copy(Users.Single(x => x.Id == userId)); OnPropertyChanged(nameof(DraftUser)); Message = "Учётная запись сохранена."; });
        _client.Changed += ClientChanged;
        _timer.Tick += TimerTick;
        Rebuild();
    }
    public void SetPassword(string password) => _password = password;
    public void SetUserPassword(string password) => _userPassword = password;
    public void Start() { _timer.Start(); if (_client.Authenticated) _ = RunAsync(_client.RefreshAsync); }
    private async void TimerTick(object? sender, EventArgs e) { if (!IsBusy && _client.Authenticated) await RunAsync(_client.RefreshAsync, true); }
    private AsyncRelayCommand Command(Func<Task> action, bool online = true)
    {
        var command = new AsyncRelayCommand(() => RunAsync(action), () => !IsBusy && (!online || Online)); _commands.Add(command); return command;
    }
    public async Task RunAsync(Func<Task> action, bool silent = false)
    {
        if (_disposed || IsBusy) return; IsBusy = true;
        try { await action(); }
        catch (InventoryWriteCommittedException e) { Message = e.Message; }
        catch (InventoryApiException e) when (e.Status == HttpStatusCode.Conflict)
        {
            try { await _client.RefreshAsync(); } catch { }
            Message = "Конфликт: " + e.Message + " Ваш черновик сохранён. Сравните его с текущей строкой; «Загрузить текущую» заменяет черновик.";
        }
        catch (Exception e) { Message = (silent ? "Не удалось обновить общую базу: " : "") + e.Message; }
        finally { IsBusy = false; }
    }
    private void Guard(Action action) { try { action(); } catch (Exception e) { Message = e.Message; } }
    private void NotifyAvailability() { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(Online)); OnPropertyChanged(nameof(ConnectionStatus)); foreach (var command in _commands) command.RaiseCanExecuteChanged(); LinkComputerCommand?.RaiseCanExecuteChanged(); UnlinkComputerCommand?.RaiseCanExecuteChanged(); NewAssetCommand?.RaiseCanExecuteChanged(); BeginLinkCommand?.RaiseCanExecuteChanged(); }
    private void ClientChanged() { if (_disposed) return; Rebuild(); NotifyAvailability(); _ = LoadPhotosAsync(); }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source) { var list = source.ToArray(); target.Clear(); foreach (var value in list) target.Add(value); }
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public string RoomText(string? id) { var r = Snapshot.Rooms.FirstOrDefault(x => x.Id == id); return r is null ? "Без кабинета" : r.Number + r.Building; }
    private void Rebuild()
    {
        if (_rebuilding) return; _rebuilding = true;
        try
        {
            var scope = _client.Connection.Url + "|" + Snapshot.InstanceId;
            if (_snapshotScope != scope)
            {
                _snapshotScope = scope; CancelLink(); ++_photoGeneration; _room = null; _selectedAsset = null; _selectedCartridge = null; _audit = null; _auditItem = null; _photo = null;
                DraftAsset = new(); DraftRoom = new(); DraftCartridge = new(); DraftAuditItem = new(); DraftUser = new(); ImportPreview = null; ImportTargets = []; Photos.Clear(); Users.Clear(); SelectedPrinter = null; ReplacementCartridge = null; SelectedUser = null; SelectedComputer = null; SelectedHistory = null; CompatibleModels = "";
                foreach (var property in new[] { nameof(DraftAsset), nameof(DraftRoom), nameof(DraftCartridge), nameof(DraftAuditItem), nameof(DraftUser), nameof(ImportPreview), nameof(ImportSummary), nameof(ReplacementCartridge), nameof(SelectedUser), nameof(SelectedComputer), nameof(CompatibleModels) }) OnPropertyChanged(property);
                _links?.ClearSnapshot();
            }
            var roomId = SelectedRoom?.Id; var assetId = SelectedAsset?.Id; var cartridgeId = SelectedCartridge?.Id; var auditId = SelectedAudit?.Id; var itemId = SelectedAuditItem?.Id;
            var draftRoomId = DraftAsset.RoomId; var actualRoomId = DraftAuditItem.ActualRoomId; var parentAssetId = DraftAsset.ParentAssetId;
            var printerId = SelectedPrinter?.Id; var replacementId = ReplacementCartridge?.Id; var computerId = SelectedComputer?.Id;
            Replace(Rooms, Snapshot.Rooms.OrderBy(x => x.Building).ThenBy(x => x.Floor).ThenBy(x => x.Number, StringComparer.CurrentCultureIgnoreCase));
            Replace(FloorRooms, Rooms.Where(x => x.Floor == Floor));
            _room = Rooms.FirstOrDefault(x => x.Id == roomId) ?? FloorRooms.FirstOrDefault();
            if (roomId is null && _room is not null && DraftRoom.Version == 0 && DraftRoom.Number.Length == 0) { DraftRoom = Copy(_room); OnPropertyChanged(nameof(DraftRoom)); }
            var all = Snapshot.Assets.Where(x => IncludeArchived || !x.Archived).Select(x => new InventoryAssetRow(x, RoomText(x.RoomId))).ToArray();
            Replace(Assets, all.Where(x => (AllRooms || x.Value.RoomId == SelectedRoom?.Id) && (Search.Length == 0 || ($"{x.Title} {x.Value.InventoryNumber} {x.Value.SerialNumber} {x.Value.Hostname} {x.Value.PhoneNumber} {x.RoomText} {x.Value.Notes}").Contains(Search, StringComparison.CurrentCultureIgnoreCase))).OrderBy(x => x.RoomText).ThenBy(x => x.Value.Seat).ThenBy(x => x.Title));
            Replace(RoomAssets, all.Where(x => x.Value.RoomId == SelectedRoom?.Id && !x.Value.Archived));
            Replace(UnplacedAssets, RoomAssets.Where(x => !x.Value.X.HasValue));
            _selectedAsset = Assets.FirstOrDefault(x => x.Id == assetId);
            Replace(Printers, Snapshot.Assets.Where(x => !x.Archived && (x.Type.Contains("принтер", StringComparison.OrdinalIgnoreCase) || x.Type.Contains("МФУ", StringComparison.OrdinalIgnoreCase))));
            Replace(Cartridges, Snapshot.Cartridges.OrderBy(x => x.Number)); _selectedCartridge = Cartridges.FirstOrDefault(x => x.Id == cartridgeId);
            var cartridgeState = CartridgeGroup switch { "Установлены" => "Установлен", "Списанные" => "Списан", _ => CartridgeGroup };
            Replace(FilteredCartridges, Cartridges.Where(x => CartridgeGroup == "Все" || x.Status == cartridgeState));
            Replace(SpareCartridges, Cartridges.Where(x => x.Status == "Запас")); OnPropertyChanged(nameof(CartridgeGroupSummary));
            LinkTargets = Snapshot.Assets.Where(x => x.Id != DraftAsset.Id && !x.Archived).Select(x => new InventoryAssetRow(x, RoomText(x.RoomId))).OrderBy(x => x.Value.RoomId != DraftAsset.RoomId).ThenBy(x => x.Title).ToArray(); OnPropertyChanged(nameof(LinkTargets));
            Replace(Audits, Snapshot.Audits.OrderByDescending(x => x.Date)); _audit = Audits.FirstOrDefault(x => x.Id == auditId);
            Replace(AuditItems, _audit?.Items ?? []); _auditItem = AuditItems.FirstOrDefault(x => x.Id == itemId);
            if (_runtime is not null) Replace(LocalComputers, _runtime.Computers.OrderBy(x => x.Name));
            // Selector bindings can write null during an ItemsSource reset. Restore the user's
            // unsaved selection, not the placement from the newly fetched server snapshot.
            DraftAsset.RoomId = draftRoomId; DraftAuditItem.ActualRoomId = actualRoomId;
            DraftAsset.ParentAssetId = parentAssetId;
            SelectedPrinter = Printers.FirstOrDefault(x => x.Id == printerId); ReplacementCartridge = Cartridges.FirstOrDefault(x => x.Id == replacementId); SelectedComputer = LocalComputers.FirstOrDefault(x => x.Id == computerId);
            foreach (var property in new[] { nameof(DraftAsset), nameof(DraftAuditItem), nameof(SelectedPrinter), nameof(ReplacementCartridge), nameof(SelectedComputer) }) OnPropertyChanged(property);
            if (_links is not null && Snapshot.InstanceId.Length > 0) _links.ApplySnapshot(Snapshot.InstanceId, Snapshot.Assets.Select(x => new InventoryPlacementProjection(x.Id, RoomText(x.RoomId), x.Seat, x.Archived, "", x.SerialNumber)));
            foreach (var property in new[] { nameof(SelectedRoom), nameof(SelectedRoomNumber), nameof(RoomTitle), nameof(SelectedAsset), nameof(SelectedCartridge), nameof(SelectedAudit), nameof(SelectedAuditItem), nameof(AuditOpen), nameof(ConnectionStatus) }) OnPropertyChanged(property);
            RefreshHistory(); LayoutChanged?.Invoke();
            OnPropertyChanged(nameof(InstalledCartridges));
        }
        finally { _rebuilding = false; }
    }
    private void RefreshHistory()
    {
        var ids = new[] { DraftAsset.Id, DraftCartridge.Id, SelectedRoom?.Id };
        Replace(History, Snapshot.History.Where(x => ids.Contains(x.EntityId)).OrderByDescending(x => x.DateUtc).Take(200));
    }
    public void ChooseMapRoom(string number) { SelectedRoom = Rooms.FirstOrDefault(x => x.Building == "Д" && x.Floor == Floor && x.Number == number); if (SelectedRoom is null) Message = "Добавьте кабинеты из планов кнопкой «Загрузить кабинеты» в подключении."; }
    public void SelectAsset(string id)
    {
        var value = Snapshot.Assets.FirstOrDefault(x => x.Id == id); if (value is null) return;
        var room = Rooms.FirstOrDefault(x => x.Id == value.RoomId); if (room is not null) { Floor = Math.Clamp(room.Floor, 1, 5); SelectedRoom = room; }
        SelectedAsset = Assets.FirstOrDefault(x => x.Id == id) ?? new(value, RoomText(value.RoomId)); SetAssetDraft(value);
    }
    private void SetAssetDraft(AssetDto asset) { DraftAsset = Copy(asset); CompatibleModels = string.Join("; ", asset.CompatibleCartridgeModels); LinkTargets = Snapshot.Assets.Where(x => x.Id != asset.Id && !x.Archived).Select(x => new InventoryAssetRow(x, RoomText(x.RoomId))).OrderBy(x => x.Value.RoomId != asset.RoomId).ThenBy(x => x.Title).ToArray(); OnPropertyChanged(nameof(LinkTargets)); DraftAsset.ParentAssetId = asset.ParentAssetId; OnPropertyChanged(nameof(DraftAsset)); OnPropertyChanged(nameof(CompatibleModels)); _ = LoadPhotosAsync(); LayoutChanged?.Invoke(); }
    public void OpenAssetCard(string id) { if (!Snapshot.Assets.Any(x => x.Id == id)) { Message = "Выберите технику на схеме или в реестре."; return; } SelectAsset(id); AssetCardRequested?.Invoke(); }
    public void ClearDraftParent() { DraftAsset.ParentAssetId = null; OnPropertyChanged(nameof(DraftAsset)); }
    public void CancelLink() { _linkSourceId = null; OnPropertyChanged(nameof(SelectionHint)); }
    public Task LinkOnMapAsync(string targetId) => RunAsync(async () =>
    {
        var id = _linkSourceId ?? throw new InvalidOperationException("Сначала выберите компонент.");
        var asset = Copy(Snapshot.Assets.Single(x => x.Id == id)); asset.ParentAssetId = targetId;
        await _client.SaveAsync("assets", asset); CancelLink(); SelectAsset(id); Message = "Устройства связаны. Связь показана линией на схеме.";
    });
    private async Task OpenCorridorAsync()
    {
        var corridor = Rooms.FirstOrDefault(x => x.Building == "Д" && x.Floor == Floor && x.Kind == "corridor");
        if (corridor is null)
        {
            if (!Online) throw new InvalidOperationException("Для первого создания коридора подключитесь к серверу.");
            var created = new RoomDto { Number = $"Коридор {Floor}", Floor = Floor, Kind = "corridor", MapKey = $"d-{Floor}-corridor" };
            await _client.PostAsync("api/rooms/seed", new SeedRoomsCommand { Rooms = [created] });
            corridor = Rooms.FirstOrDefault(x => x.Floor == Floor && x.Kind == "corridor");
            if (corridor is null) throw new InvalidOperationException("Номер коридора уже занят обычным помещением. Переименуйте его.");
        }
        SelectedRoom = corridor;
    }
    private async Task SaveAssetAsync()
    {
        var asset = Copy(DraftAsset); asset.CompatibleCartridgeModels = CompatibleModels.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var old = Snapshot.Assets.FirstOrDefault(x => x.Id == asset.Id); if (old?.RoomId != asset.RoomId) asset.X = asset.Y = null;
        await _client.SaveAsync("assets", asset); SelectAsset(asset.Id); Message = "Карточка сохранена.";
    }
    public Task PlaceAsync(string assetId, double x, double y) => RunAsync(async () =>
    {
        if (!CanEdit && !Online) throw new InvalidOperationException("Подключитесь к серверу.");
        var asset = Copy(Snapshot.Assets.Single(a => a.Id == assetId)); asset.X = Math.Clamp(x, 0, 1); asset.Y = Math.Clamp(y, 0, 1);
        var room = Snapshot.Rooms.FirstOrDefault(r => r.Id == asset.RoomId);
        if (room?.Kind == "corridor")
        {
            var map = CorpusMaps.Floors[Math.Clamp(room.Floor, 1, 5) - 1]; var b = map.Drawing.Bounds;
            if (!map.Corridor.FillContains(new Point(b.X + asset.X.Value * b.Width, b.Y + asset.Y.Value * b.Height))) throw new InvalidOperationException("Выберите точку в голубой зоне коридора.");
        }
        await _client.SaveAsync("assets", asset); SelectAsset(assetId); Message = "Позиция на схеме сохранена.";
    });
    private async Task ChangeCartridgeAsync(bool remove)
    {
        var printer = SelectedPrinter ?? throw new InvalidOperationException("Выберите принтер.");
        printer = Snapshot.Assets.Single(x => x.Id == printer.Id);
        var old = Snapshot.Cartridges.SingleOrDefault(x => x.PrinterId == printer.Id && x.Slot.Equals(CartridgeSlot, StringComparison.OrdinalIgnoreCase));
        var next = remove ? null : Snapshot.Cartridges.FirstOrDefault(x => x.Id == ReplacementCartridge?.Id) ?? throw new InvalidOperationException("Выберите устанавливаемый картридж.");
        await _client.PostAsync("api/cartridges/replace", new ReplaceCartridgeCommand { PrinterId = printer.Id, PrinterVersion = printer.Version, Slot = CartridgeSlot, OldCartridgeId = old?.Id, OldCartridgeVersion = old?.Version ?? 0, NewCartridgeId = next?.Id, NewCartridgeVersion = next?.Version ?? 0, RemovedStatus = RemovedCartridgeStatus });
        Message = remove ? "Картридж снят." : "Замена картриджа сохранена.";
    }
    private AuditDto RequireAudit() => SelectedAudit is { CompletedUtc: null } audit ? audit : throw new InvalidOperationException("Выберите незавершённую проверку.");
    private async Task ApplyAuditMoveAsync()
    {
        var item = SelectedAuditItem ?? throw new InvalidOperationException("Выберите результат проверки.");
        if (DraftAuditItem.Id != item.Id || DraftAuditItem.ActualRoomId != item.ActualRoomId || DraftAuditItem.Result != item.Result || DraftAuditItem.Comment != item.Comment)
            throw new InvalidOperationException("Сначала сохраните изменённый результат проверки, затем примените перемещение.");
        if (string.IsNullOrEmpty(item.ActualRoomId) || item.Result is not ("Найдено" or "Обнаружено в другом кабинете"))
            throw new InvalidOperationException("Сохраните результат «Найдено» или «Обнаружено в другом кабинете» и фактический кабинет.");
        var asset = Copy(Snapshot.Assets.Single(x => x.Id == item.AssetId));
        if (asset.RoomId == item.ActualRoomId) { Message = "Оборудование уже находится в этом кабинете. Расстановка сохранена."; return; }
        asset.RoomId = item.ActualRoomId; asset.X = asset.Y = null;
        await _client.SaveAsync("assets", asset); Message = "Размещение оборудования изменено. История проверки сохранена.";
    }
    private async Task CreateAuditAsync()
    {
        if (AuditScope == "Кабинет" && SelectedRoom is null) throw new InvalidOperationException("Выберите кабинет на карте.");
        var audit = new AuditDto { Name = AuditName, Date = new DateTimeOffset(AuditDate), Scope = AuditScope switch { "Этаж" => "floor", "Кабинет" => "room", _ => "building" }, Building = SelectedRoom?.Building ?? "Д", Floor = Floor, RoomId = SelectedRoom?.Id };
        await _client.SaveAsync("audits", audit); SelectAudit(audit.Id); Message = "Проверка создана. Ожидаемое размещение зафиксировано.";
    }
    private void SelectAudit(string id, string? itemId = null) { SelectedAudit = Audits.FirstOrDefault(x => x.Id == id); SelectedAuditItem = AuditItems.FirstOrDefault(x => x.Id == itemId) ?? AuditItems.FirstOrDefault(); if (SelectedAuditItem is not null) { DraftAuditItem = Copy(SelectedAuditItem); OnPropertyChanged(nameof(DraftAuditItem)); } }
    private (string type, string? id) PhotoOwner() => PhotoScope switch { "Кабинет" => ("room", SelectedRoom?.Id), "Проверка" => ("auditItem", SelectedAuditItem?.Id), _ => ("asset", DraftAsset.Version == 0 ? null : DraftAsset.Id) };
    public async Task LoadPhotosAsync()
    {
        var generation = ++_photoGeneration; var (type, id) = PhotoOwner(); var selectedId = SelectedPhoto?.Value.Id; var caption = PhotoCaption; Photos.Clear();
        if (id is null) { SelectedPhoto = null; return; }
        foreach (var photo in Snapshot.Photos.Where(x => x.OwnerType == type && x.OwnerId == id).OrderByDescending(x => x.IsPrimary))
        {
            string? path = null; BitmapImage? image = null;
            try
            {
                path = await _client.PhotoPathAsync(photo);
                if (path is not null) { image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 320; image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); }
            }
            catch (Exception e) { if (generation == _photoGeneration) Message = "Фотография недоступна: " + e.Message; }
            if (generation != _photoGeneration || _disposed) return;
            Photos.Add(new(photo, image, path));
        }
        SelectedPhoto = Photos.FirstOrDefault(x => x.Value.Id == selectedId) ?? Photos.FirstOrDefault();
        if (SelectedPhoto is not null && SelectedPhoto.Value.Id == selectedId) { PhotoCaption = caption; OnPropertyChanged(nameof(PhotoCaption)); }
    }
    private async Task UploadPhotoAsync()
    {
        var (type, id) = PhotoOwner(); if (id is null) throw new InvalidOperationException("Сначала выберите сохранённый объект для фотографии.");
        var dialog = new OpenFileDialog { Filter = "Фотографии (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        foreach (var file in dialog.FileNames) await _client.UploadPhotoAsync(file, type, id, PhotoCaption);
        await LoadPhotosAsync(); Message = "Фотографии загружены в общее хранилище.";
    }
    private async Task ReadImportAsync()
    {
        if (ImportPath.Length == 0) return;
        var snapshot = Copy(Snapshot); ImportPreview = await Task.Run(() => InventorySpreadsheet.ReadPreview(ImportPath, snapshot, ImportOptions)); ImportTargets = snapshot.Assets.Where(x => !x.Archived).Select(x => new InventoryAssetRow(x, RoomText(x.RoomId))).OrderBy(x => x.Summary).ToArray(); OnPropertyChanged(nameof(ImportTargets)); OnPropertyChanged(nameof(ImportPreview)); OnPropertyChanged(nameof(ImportSummary));
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); _timer.Tick -= TimerTick; _client.Changed -= ClientChanged; _links?.Dispose(); _client.Dispose(); }
}
