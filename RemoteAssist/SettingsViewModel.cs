using System.Security;

namespace RemoteAssist;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly DirectoryConfiguration _configuration;
    private readonly IActiveDirectoryService _directory;
    private SecureString? _password;
    private bool _removePassword;
    private CancellationTokenSource? _testCancellation;
    private string _message = "Настройки применяются после нажатия «Сохранить».";
    private string _category = "Подключение";
    public UserSettings Draft { get; private set; } = new();
    public string MasksText { get; set; } = "";
    public string LoadUpdateMasksText { get; set; } = "";
    public string Category
    {
        get => _category;
        set { if (Set(ref _category, value)) CategoryChanged?.Invoke(); }
    }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string PasswordHint => _removePassword ? "Сохранённый пароль будет удалён при сохранении настроек." : "Оставьте поле пустым, чтобы использовать ранее сохранённый пароль.";
    public AsyncRelayCommand TestCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand DeletePasswordCommand { get; }
    public RelayCommand NavigateCategoryCommand { get; }
    public event Action? PasswordResetRequested;
    public event Action? Saved;
    public event Action? CategoryChanged;

    public SettingsViewModel(DirectoryConfiguration configuration, IActiveDirectoryService directory)
    {
        _configuration = configuration;
        _directory = directory;
        NavigateCategoryCommand = new RelayCommand(value =>
        {
            if (value is string category && category is "Подключение" or "Рабочие столы" or "Обновление нагрузки" or "Оформление") Category = category;
        });
        TestCommand = new AsyncRelayCommand(TestAsync);
        SaveCommand = new RelayCommand(_ => Save());
        CancelCommand = new RelayCommand(_ => Reset());
        DeletePasswordCommand = new RelayCommand(_ =>
        {
            _removePassword = true;
            _password?.Dispose(); _password = null;
            PasswordResetRequested?.Invoke();
            OnPropertyChanged(nameof(PasswordHint));
            Message = "Нажмите «Сохранить», чтобы удалить пароль из Windows, или «Отмена».";
        });
        Reset();
    }
    public void SetPassword(SecureString password)
    {
        _password?.Dispose(); _password = password.Copy();
        if (password.Length > 0) _removePassword = false;
        OnPropertyChanged(nameof(PasswordHint));
    }
    public void Reset()
    {
        _testCancellation?.Cancel();
        _password?.Dispose(); _password = null;
        _removePassword = false;
        Draft = _configuration.Settings.Copy();
        MasksText = string.Join(Environment.NewLine, Draft.ComputerMasks);
        LoadUpdateMasksText = string.Join(Environment.NewLine, Draft.LoadUpdateMasks);
        OnPropertyChanged(nameof(Draft)); OnPropertyChanged(nameof(MasksText)); OnPropertyChanged(nameof(LoadUpdateMasksText)); OnPropertyChanged(nameof(PasswordHint));
        PasswordResetRequested?.Invoke();
        Message = "Настройки применяются после нажатия «Сохранить».";
    }
    private UserSettings Candidate()
    {
        var candidate = Draft.Copy();
        candidate.DomainController = string.IsNullOrWhiteSpace(candidate.DomainController) ? null : candidate.DomainController.Trim();
        candidate.SearchBaseDn = string.IsNullOrWhiteSpace(candidate.SearchBaseDn) ? null : candidate.SearchBaseDn.Trim();
        candidate.AdUserName = candidate.AdUserName.Trim();
        try { candidate.ComputerMasks = DirectoryFilters.ParseMasks(MasksText); }
        catch (InvalidOperationException ex) { Category = "Рабочие столы"; throw new InvalidOperationException("Рабочие столы: " + ex.Message); }
        try { candidate.LoadUpdateMasks = string.IsNullOrWhiteSpace(LoadUpdateMasksText) ? [] : DirectoryFilters.ParseMasks(LoadUpdateMasksText); }
        catch (InvalidOperationException ex) { Category = "Обновление нагрузки"; throw new InvalidOperationException("Обновление нагрузки: " + ex.Message); }
        if (candidate.UseExplicitCredentials)
        {
            using var empty = new SecureString();
            DirectoryFilters.Credentials(candidate.AdUserName, empty);
        }
        return candidate;
    }
    private void Save()
    {
        try
        {
            _testCancellation?.Cancel();
            _configuration.Save(Candidate(), _password, _removePassword);
            Reset();
            Message = "Настройки сохранены.";
            Saved?.Invoke();
        }
        catch (Exception ex) { Message = DirectoryErrors.Describe(ex); }
    }
    private async Task TestAsync()
    {
        _testCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _testCancellation = cancellation;
        try
        {
            var candidate = Candidate();
            using var password = _password is { Length: > 0 } ? _password.Copy() : _removePassword ? null : _configuration.GetPassword(candidate);
            Message = "Проверка подключения к Active Directory…";
            var message = await _directory.TestConnectionAsync(candidate, password, cancellation.Token);
            if (!cancellation.IsCancellationRequested) Message = message;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) Message = DirectoryErrors.Describe(ex); }
        finally { if (_testCancellation == cancellation) _testCancellation = null; }
    }
    public void Dispose() { _testCancellation?.Cancel(); _password?.Dispose(); }
}
