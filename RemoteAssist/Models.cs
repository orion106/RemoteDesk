namespace RemoteAssist;

public sealed record AdComputer(string Name, string? DnsHostName, string DistinguishedName);
public sealed record AdUser(string SamAccountName, string? UserPrincipalName, string DisplayName);
public sealed record UserSession(string Computer, string? Domain, string? UserName, int Id, string State, DateTime CheckedAt, string? Error)
{
    public bool CanConnect => Error is null && Id >= 0 && State.Equals("Active", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(UserName);
    public string DomainUser => string.IsNullOrWhiteSpace(UserName) ? "—" : string.IsNullOrWhiteSpace(Domain) ? UserName : $"{Domain}\\{UserName}";
}

public sealed class UserSettings : ObservableObject
{
    private string? _domainController, _searchBaseDn;
    private string _theme = "Тёмная", _adUserName = "";
    private bool _useExplicitCredentials, _rememberPassword = true;
    public string? DomainController { get => _domainController; set => Set(ref _domainController, value); }
    public string? SearchBaseDn { get => _searchBaseDn; set => Set(ref _searchBaseDn, value); }
    public string Theme { get => _theme; set => Set(ref _theme, value); }
    public bool UseExplicitCredentials { get => _useExplicitCredentials; set => Set(ref _useExplicitCredentials, value); }
    public string AdUserName { get => _adUserName; set => Set(ref _adUserName, value); }
    public bool RememberPassword { get => _rememberPassword; set => Set(ref _rememberPassword, value); }
    public string[] ComputerMasks { get; set; } = ["dc*-*", "note-dc*-*"];
    public string[] LoadUpdateMasks { get; set; } = [];
    public LabOptions Lab { get; set; } = new();
    public UserSettings Copy() => new()
    {
        DomainController = DomainController, SearchBaseDn = SearchBaseDn, Theme = Theme,
        UseExplicitCredentials = UseExplicitCredentials, AdUserName = AdUserName,
        RememberPassword = RememberPassword, ComputerMasks = [.. ComputerMasks], LoadUpdateMasks = [.. LoadUpdateMasks], Lab = (Lab ?? new()).Copy()
    };
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAssist", "settings.json");
    public static UserSettings Load(string? path = null)
    {
        try
        {
            var result = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path ?? FilePath)) ?? new();
            if (result.ComputerMasks is not { Length: > 0 }) result.ComputerMasks = ["dc*-*", "note-dc*-*"];
            result.LoadUpdateMasks ??= [];
            result.Lab ??= new();
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
