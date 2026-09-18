using Microsoft.Win32;

namespace RemoteAssist;

public sealed class LoadUpdateViewModel : ObservableObject, IDisposable
{
    private readonly IActiveDirectoryService _directory;
    private readonly DirectoryConfiguration _configuration;
    private readonly IRemoteUpdateSessionFactory _factory;
    private CancellationTokenSource? _cancellation;
    private Task _active = Task.CompletedTask;
    private LoadUpdatePackage? _package;
    private bool _busy, _normalRun, _disposed, _invalidate;
    private string _message = "Выберите Service.exe и найдите установленные программы. Маски компьютеров задаются в настройках.";
    private int _completed, _total;
    public ObservableCollection<LoadUpdateRow> Results { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public string MaskSummary => _configuration.Settings.LoadUpdateMasks.Length == 0
        ? "Маски не заданы — откройте Настройки → Обновление нагрузки."
        : string.Join("  ·  ", _configuration.Settings.LoadUpdateMasks);
    public string Message { get => _message; private set => Set(ref _message, value); }
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); OnPropertyChanged(nameof(CanEdit)); RefreshCommands(); } }
    public bool CanEdit => !IsBusy && !_disposed;
    public string SourcePath => _package?.SourcePath ?? "Файл не выбран";
    public string PackageDescription => _package is null ? "Выберите новый Service.exe на этом ПК или в сетевой папке." : $"Версия: {_package.Version}\nSHA-256: {_package.Hash}";
    public string ProgressText => _total == 0 ? Message : $"{Message} · Обработано {_completed} из {_total}";
    public double ProgressPercent => _total == 0 ? 0 : _completed * 100.0 / _total;
    public string Account => _configuration.Settings.UseExplicitCredentials ? _configuration.Settings.AdUserName : $@"{Environment.UserDomainName}\{Environment.UserName}";
    public AsyncRelayCommand ChooseFileCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public AsyncRelayCommand ForceRequestCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    public LoadUpdateViewModel(IActiveDirectoryService directory, DirectoryConfiguration configuration, IRemoteUpdateSessionFactory? factory = null)
    {
        _directory = directory; _configuration = configuration; _factory = factory ?? new RemoteUpdateSessionFactory();
        ChooseFileCommand = new AsyncRelayCommand(async () =>
        {
            var dialog = new OpenFileDialog { Title = "Выберите новый Service.exe", Filter = "Программа нагрузки (Service.exe)|Service.exe", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog() == true) await SelectPackageAsync(dialog.FileName);
        }, () => CanEdit);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => CanEdit);
        UpdateCommand = new AsyncRelayCommand(() => UpdateAsync(false), () => CanEdit && _package is not null && Results.Any(r => r.Selected));
        ForceRequestCommand = new AsyncRelayCommand(() => UpdateAsync(true), () => CanEdit && _package is not null && _normalRun && Results.Any(r => r.Selected && r.Result.State == LoadUpdateState.Busy));
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        SelectAllCommand = new RelayCommand(_ => { foreach (var row in Results) row.Selected = true; }, _ => CanEdit && Results.Any(r => r.CanSelect));
        SelectNoneCommand = new RelayCommand(_ => { foreach (var row in Results) row.Selected = false; }, _ => CanEdit && Results.Any(r => r.Selected));
        _configuration.Changed += ConfigurationChanged;
    }
    private void ConfigurationChanged()
    {
        OnPropertyChanged(nameof(Account)); OnPropertyChanged(nameof(MaskSummary));
        Invalidate();
    }
    private void Invalidate()
    {
        _normalRun = false;
        if (IsBusy) { _invalidate = true; Cancel(); }
        else { ClearRows(); SetMessage("Настройки изменены. Выполните поиск заново."); }
    }
    private void ClearRows()
    {
        foreach (var row in Results) row.PropertyChanged -= RowChanged;
        Results.Clear(); _total = _completed = 0; _normalRun = false; NotifyProgress(); RefreshCommands();
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(LoadUpdateRow.Selected)) RefreshCommands(); }
    private void Add(LoadInstallation installation)
    {
        var row = new LoadUpdateRow(installation) { NewVersion = _package?.Version ?? "—" };
        row.PropertyChanged += RowChanged; Results.Add(row);
        AppendLog($"{row.Computer} · {row.Path} · {row.Status} {row.Detail}");
    }
    public Task SelectPackageAsync(string path) => RunAsync(async token =>
    {
        SetMessage("Проверка и подготовка нового Service.exe…");
        var candidate = await Task.Run(() => LoadUpdatePackage.Create(path, token), token);
        _package?.Dispose(); _package = candidate; _normalRun = false;
        foreach (var row in Results)
        {
            row.NewVersion = candidate.Version;
            if (row.CanSelect) row.Apply(new(LoadUpdateState.Found, "Выбран новый файл обновления."));
        }
        OnPropertyChanged(nameof(SourcePath)); OnPropertyChanged(nameof(PackageDescription));
        SetMessage("Файл проверен. Можно обновить найденные установки.");
    });

    public Task ScanAsync()
    {
        if (!CanEdit) return Task.CompletedTask;
        if (_configuration.Settings.LoadUpdateMasks.Length == 0)
        {
            SetMessage("Укажите и сохраните маски в меню «Настройки» → «Обновление нагрузки».");
            return Task.CompletedTask;
        }
        return RunAsync(async token =>
        {
            ClearRows(); SetMessage("Поиск компьютеров в AD…");
            var masks = _configuration.Settings.LoadUpdateMasks.ToArray();
            var found = await _directory.FindComputersAsync("", masks, token);
            token.ThrowIfCancellationRequested();
            var computers = found.Where(c => DirectoryFilters.AllowsComputer(c.Name, masks))
                .GroupBy(c => c.DnsHostName ?? c.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            _total = computers.Length; NotifyProgress();
            using var session = _factory.Open(_configuration);
            using var gate = new SemaphoreSlim(4);
            SetMessage("Поиск Service.exe на компьютерах…");
            await Task.WhenAll(computers.Select(async computer =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var host = computer.DnsHostName ?? computer.Name;
                    try
                    {
                        var installations = await session.ScanAsync(host, token);
                        token.ThrowIfCancellationRequested();
                        foreach (var item in installations) Add(item);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { var error = LoadUpdateErrors.Describe(ex); Add(new(host, "", "—", error.State, error.Detail)); }
                    _completed++; NotifyProgress();
                }
                finally { gate.Release(); }
            }));
            SetMessage(computers.Length == 0 ? "Нет компьютеров, соответствующих маскам обновления." : $"Поиск завершён. Установок найдено: {Results.Count(r => r.CanSelect)}.");
        });
    }

    public Task UpdateAsync(bool requestConsent)
    {
        if (!CanEdit || _package is null || (requestConsent && !_normalRun)) return Task.CompletedTask;
        var rows = Results.Where(r => r.Selected && (!requestConsent || r.Result.State == LoadUpdateState.Busy)).ToArray();
        if (rows.Length == 0) return Task.CompletedTask;
        return RunAsync(async token =>
        {
            using var session = _factory.Open(_configuration);
            using var gate = new SemaphoreSlim(4);
            var package = _package!;
            _completed = 0; _total = rows.Length; NotifyProgress();
            SetMessage(requestConsent ? "Запрос согласия пользователей. Ожидание ответа — до пяти минут…" : "Обновление выбранных установок…");
            await Task.WhenAll(rows.GroupBy(r => r.Computer, StringComparer.OrdinalIgnoreCase).Select(async group =>
            {
                await gate.WaitAsync(token);
                try
                {
                    using var machineLease = await MachineMutationGate.AcquireHostAsync(group.Key, token);
                    foreach (var row in group)
                    {
                        token.ThrowIfCancellationRequested();
                        LoadUpdateResult result;
                        try { result = await session.UpdateAsync(row.Installation, package, requestConsent, token); }
                        catch (Exception ex) { result = LoadUpdateErrors.Describe(ex); }
                        // A completed atomic replacement remains visible even when cancellation arrives.
                        row.Apply(result, result.State is LoadUpdateState.Updated or LoadUpdateState.Current ? package.Version : null);
                        _completed++; NotifyProgress();
                        AppendLog($"{row.Computer} · {row.Path} · {row.Status} · {row.Detail}");
                    }
                }
                finally { gate.Release(); }
            }));
            if (!requestConsent) _normalRun = true;
            var success = rows.Count(r => r.Result.State is LoadUpdateState.Updated or LoadUpdateState.Current);
            SetMessage($"Обработка завершена. Обновлены или совпадают: {success}; требуют внимания: {rows.Length - success}.");
        });
    }
    private Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (!CanEdit) return Task.CompletedTask;
        _active = RunCoreAsync(work);
        return _active;
    }
    private async Task RunCoreAsync(Func<CancellationToken, Task> work)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation; IsBusy = true;
        try { await work(cancellation.Token); }
        catch (OperationCanceledException) { SetMessage("Операция отменена. Выполненные действия показаны в результатах и журнале."); }
        catch (Exception ex) { SetMessage(LoadUpdateErrors.Describe(ex).Detail); AppendLog(Message); }
        finally
        {
            _cancellation = null;
            if (_invalidate) { _invalidate = false; ClearRows(); SetMessage("Настройки изменены. Выполните поиск заново; результаты завершённых действий сохранены в журнале."); }
            IsBusy = false;
            if (_disposed) { _package?.Dispose(); _package = null; }
        }
    }
    public void Cancel()
    {
        _cancellation?.Cancel();
        SetMessage("Отмена: новые действия остановлены. Ожидаем завершения текущего сетевого запроса или безопасной замены…");
    }
    public async Task CancelAndWaitAsync() { Cancel(); await _active; }
    private void SetMessage(string message) { Message = message; NotifyProgress(); }
    private void NotifyProgress() { OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(ProgressPercent)); }
    private void AppendLog(string text)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        if (Log.Count > 500) Log.RemoveAt(Log.Count - 1);
    }
    private void RefreshCommands()
    {
        ChooseFileCommand?.RaiseCanExecuteChanged(); ScanCommand?.RaiseCanExecuteChanged();
        UpdateCommand?.RaiseCanExecuteChanged(); ForceRequestCommand?.RaiseCanExecuteChanged(); CancelCommand?.RaiseCanExecuteChanged();
        SelectAllCommand?.RaiseCanExecuteChanged(); SelectNoneCommand?.RaiseCanExecuteChanged();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _cancellation?.Cancel();
        _configuration.Changed -= ConfigurationChanged;
        foreach (var row in Results) row.PropertyChanged -= RowChanged;
        if (!IsBusy) { _package?.Dispose(); _package = null; }
    }
}
