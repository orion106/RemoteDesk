using System.Windows.Threading;

namespace RemoteAssist;

public sealed class SessionRow : ObservableObject
{
    private UserSession _session;
    public SessionRow(UserSession session, string displayName = "") { _session = session; DisplayName = displayName; }
    public UserSession Session => _session;
    public string Computer => _session.Computer;
    public string DisplayName { get; }
    public string DomainUser => _session.DomainUser;
    public string SessionId => _session.Id < 0 ? "—" : _session.Id.ToString();
    public string State => _session.State.ToLowerInvariant() switch
    {
        "active" => "Активен", "disc" or "disconnected" or "откл" => "Отключён", "idle" => "Ожидание", _ => _session.State
    };
    public DateTime CheckedAt => _session.CheckedAt;
    public string Status => _session.Error ?? (_session.Id < 0 ? "Нет пользовательских сеансов" : "Готово");
    public bool CanConnect => _session.CanConnect;
    public bool HasError => _session.Error is not null;
    public void Update(UserSession session)
    {
        _session = session;
        OnPropertyChanged("");
    }
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IActiveDirectoryService _ad;
    private readonly ISessionService _sessions;
    private readonly IRemoteDesktopLauncher _rdp;
    private readonly DirectoryConfiguration _configuration;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private CancellationTokenSource? _cts;
    private int _version;
    private string _query = "";
    private bool _searchByComputer = true, _isBusy, _connectionBusy, _sidebarExpanded = true;
    private string _section = "Главная", _status = "Готово к поиску", _scanState = "Опрос ещё не запускался";
    private int _completed, _total, _errors, _activeSessions;

    public ObservableCollection<SessionRow> Results { get; } = new();
    public ICollectionView ResultsView { get; }
    public SettingsViewModel Settings { get; }
    public LoadUpdateViewModel LoadUpdate { get; }
    public LabViewModel? Lab { get; }
    public InventoryViewModel? Inventory { get; }
    public string Query { get => _query; set => Set(ref _query, value); }
    public bool SearchByComputer { get => _searchByComputer; set => Set(ref _searchByComputer, value); }
    public bool SidebarExpanded { get => _sidebarExpanded; set => Set(ref _sidebarExpanded, value); }
    public string Section { get => _section; set => Set(ref _section, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            SearchCommand.RaiseCanExecuteChanged(); RefreshCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged();
        }
    }
    public string Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(ProgressText)); } }
    public string ScanState { get => _scanState; private set => Set(ref _scanState, value); }
    public int Completed { get => _completed; private set { Set(ref _completed, value); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(ProgressPercent)); } }
    public int Total { get => _total; private set { Set(ref _total, value); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(ProgressPercent)); } }
    public int Errors { get => _errors; private set { Set(ref _errors, value); OnPropertyChanged(nameof(ProgressText)); } }
    public int ActiveSessions { get => _activeSessions; private set => Set(ref _activeSessions, value); }
    public double ProgressPercent => Total == 0 ? 0 : Completed * 100.0 / Total;
    public string ProgressText => Total == 0 ? Status : $"{Status} · Проверено {Completed} из {Total} · Ошибок: {Errors}";
    public string WindowsAccount => $@"{Environment.UserDomainName}\{Environment.UserName}";
    public string AdAccount => _configuration.Settings.UseExplicitCredentials ? _configuration.Settings.AdUserName : WindowsAccount;
    public string ProfileTooltip => $"Windows: {WindowsAccount}\nActive Directory: {AdAccount}\nОткрыть настройки учётной записи";
    public string MaskSummary => string.Join("  ·  ", _configuration.Settings.ComputerMasks);
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand OpenUpdateSettingsCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }

    public MainViewModel(IActiveDirectoryService ad, ISessionService sessions, IRemoteDesktopLauncher rdp, DirectoryConfiguration configuration, IRemoteUpdateSessionFactory? updateFactory = null, LabViewModel? lab = null, InventoryViewModel? inventory = null)
    {
        _ad = ad; _sessions = sessions; _rdp = rdp; _configuration = configuration;
        Lab = lab;
        Inventory = inventory;
        ResultsView = CollectionViewSource.GetDefaultView(Results);
        ResultsView.SortDescriptions.Add(new SortDescription(nameof(SessionRow.Computer), ListSortDirection.Ascending));
        SearchCommand = new AsyncRelayCommand(() => SearchAsync(false), () => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(() => SearchAsync(true), () => !IsBusy);
        CancelCommand = new RelayCommand(_ => { _cts?.Cancel(); Status = "Остановка опроса…"; }, _ => IsBusy);
        ConnectCommand = new RelayCommand(async row => await ConnectAsync(row as SessionRow), row => !_connectionBusy && row is SessionRow { CanConnect: true });
        NavigateCommand = new RelayCommand(value => Section = value as string ?? "Главная");
        ToggleSidebarCommand = new RelayCommand(_ => SidebarExpanded = !SidebarExpanded);
        Settings = new SettingsViewModel(configuration, ad);
        LoadUpdate = new LoadUpdateViewModel(ad, configuration, updateFactory);
        OpenUpdateSettingsCommand = new RelayCommand(_ => { Settings.Category = "Обновление нагрузки"; Section = "Настройки"; });
        _configuration.Changed += OnConfigurationChanged;
    }

    private void OnConfigurationChanged()
    {
        _cts?.Cancel();
        _version++;
        Results.Clear();
        Completed = Total = Errors = ActiveSessions = 0;
        IsBusy = false;
        ScanState = "Настройки изменены — выполните новый поиск";
        Status = "Настройки применены. Результаты очищены.";
        if (_sessions is SessionService sessions) sessions.ClearCache();
        OnPropertyChanged(nameof(AdAccount)); OnPropertyChanged(nameof(ProfileTooltip)); OnPropertyChanged(nameof(MaskSummary));
    }

    public async Task SearchAsync(bool force)
    {
        if (IsBusy) return;
        if (!SearchByComputer && string.IsNullOrWhiteSpace(Query))
        {
            Status = "Введите ФИО, логин или UPN пользователя.";
            return;
        }
        var version = ++_version;
        using var cancellation = new CancellationTokenSource();
        _cts = cancellation;
        var token = cancellation.Token;
        var searchText = Query.Trim();
        var byComputer = SearchByComputer;
        Results.Clear();
        Completed = Errors = Total = ActiveSessions = 0;
        IsBusy = true;
        ScanState = "Опрос выполняется — результаты неполные";
        try
        {
            IReadOnlyList<AdComputer> computers;
            Dictionary<string, AdUser>? users = null;
            Status = byComputer ? "Поиск компьютеров в AD…" : "Поиск пользователей в AD…";
            if (byComputer) computers = await _ad.FindComputersAsync(searchText, token);
            else
            {
                var found = await _ad.FindUsersAsync(searchText, token);
                token.ThrowIfCancellationRequested();
                users = found.GroupBy(u => u.SamAccountName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                if (users.Count == 0) { Status = "Пользователи не найдены."; ScanState = "Поиск завершён"; return; }
                computers = await _ad.GetSearchComputersAsync(token);
            }
            token.ThrowIfCancellationRequested();
            computers = computers.Where(c => DirectoryFilters.AllowsComputer(c.Name, _configuration.Settings.ComputerMasks)).ToArray();
            Total = computers.Count;
            Status = Total == 0 ? "Нет компьютеров, соответствующих маскам и запросу." : "Опрос сеансов…";
            using var gate = new SemaphoreSlim(8);
            await Task.WhenAll(computers.Select(async computer =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var sessions = await _sessions.GetSessionsAsync(computer.DnsHostName ?? computer.Name, token, force);
                    token.ThrowIfCancellationRequested();
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (version != _version || token.IsCancellationRequested) return;
                        // Count errors per computer before filtering by the searched user.
                        if (sessions.Any(s => s.Error is not null)) Errors++;
                        ActiveSessions += sessions.Count(s => s.State.Equals("Active", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.UserName));
                        AddResults(sessions, users);
                        Completed++;
                    });
                }
                finally { gate.Release(); }
            }));
            token.ThrowIfCancellationRequested();
            if (users is not null) AddMissingUsers(users);
            Status = Total == 0 ? "Нет компьютеров, соответствующих маскам и запросу." : Errors > 0 ? "Опрос завершён с ошибками." : "Опрос завершён.";
            ScanState = Errors > 0 ? "Опрос неполный: часть компьютеров недоступна" : "Опрос завершён";
        }
        catch (OperationCanceledException)
        {
            if (version == _version) { Status = "Поиск отменён. Показаны полученные результаты."; ScanState = "Опрос отменён — результаты неполные"; }
        }
        catch (Exception ex)
        {
            if (version == _version) { Status = DirectoryErrors.Describe(ex); ScanState = "Опрос не завершён из-за ошибки"; }
        }
        finally
        {
            if (version == _version) IsBusy = false;
            if (_cts == cancellation) _cts = null;
        }
    }

    private void AddResults(IReadOnlyList<UserSession> sessions, Dictionary<string, AdUser>? users)
    {
        foreach (var session in sessions)
        {
            if (users is not null && session.Error is null && (session.UserName is null || !users.ContainsKey(session.UserName))) continue;
            var name = session.UserName is not null && users?.TryGetValue(session.UserName, out var user) == true ? user.DisplayName : "";
            Results.Add(new SessionRow(session, name));
        }
    }

    private void AddMissingUsers(Dictionary<string, AdUser> users)
    {
        var present = Results.Where(x => x.Session.Id >= 0 && x.Session.Error is null).Select(x => x.Session.UserName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users.Values.Where(user => !present.Contains(user.SamAccountName)))
            Results.Add(new SessionRow(new UserSession("—", null, user.SamAccountName, -1, "Не найден", DateTime.Now,
                Errors > 0 ? "Сеанс не найден. Проверены не все компьютеры." : "Пользовательский сеанс не найден."), user.DisplayName));
    }

    private async Task ConnectAsync(SessionRow? row)
    {
        if (row is null || _connectionBusy) return;
        var version = _version;
        _connectionBusy = true;
        ConnectCommand.RaiseCanExecuteChanged();
        try
        {
            var latest = await _sessions.GetSessionsAsync(row.Computer, CancellationToken.None, true);
            if (version != _version) { Status = "Результаты изменились. Выберите сеанс заново."; return; }
            var actual = latest.FirstOrDefault(s => s.Id == row.Session.Id &&
                string.Equals(s.UserName, row.Session.UserName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Domain, row.Session.Domain, StringComparison.OrdinalIgnoreCase));
            if (actual is null || !actual.CanConnect)
            {
                row.Update(actual ?? row.Session with { Error = "Сеанс изменился. Обновите результаты.", State = "Недоступен", CheckedAt = DateTime.Now });
                Status = "Сеанс изменился или недоступен.";
                return;
            }
            row.Update(actual);
            _rdp.Launch(actual);
            Status = "Запущено подключение.";
        }
        catch { Status = "Не удалось запустить подключение. Проверьте доступность ПК и права текущего пользователя Windows."; }
        finally { _connectionBusy = false; ConnectCommand.RaiseCanExecuteChanged(); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _configuration.Changed -= OnConfigurationChanged;
        Settings.Dispose();
        LoadUpdate.Dispose();
        Inventory?.Dispose();
        Lab?.Dispose();
    }
}
