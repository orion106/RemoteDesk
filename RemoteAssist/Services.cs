using System.DirectoryServices.Protocols;
using System.Security;

namespace RemoteAssist;

public interface IActiveDirectoryService
{
    Task<IReadOnlyList<AdComputer>> FindComputersAsync(string query, CancellationToken token);
    Task<IReadOnlyList<AdComputer>> FindComputersAsync(string query, IReadOnlyList<string> masks, CancellationToken token);
    Task<IReadOnlyList<AdUser>> FindUsersAsync(string query, CancellationToken token);
    Task<IReadOnlyList<AdComputer>> GetSearchComputersAsync(CancellationToken token);
    Task<string> TestConnectionAsync(UserSettings settings, SecureString? password, CancellationToken token);
}
public interface ISessionService
{
    Task<IReadOnlyList<UserSession>> GetSessionsAsync(string computer, CancellationToken token, bool force = false);
}
public interface IRemoteDesktopLauncher { void Launch(UserSession session); }

public sealed class ActiveDirectoryService(DirectoryConfiguration configuration) : IActiveDirectoryService
{
    public Task<IReadOnlyList<AdComputer>> FindComputersAsync(string query, CancellationToken token)
        => FindComputersAsync(query, configuration.Settings.ComputerMasks, token);

    public Task<IReadOnlyList<AdComputer>> FindComputersAsync(string query, IReadOnlyList<string> masks, CancellationToken token)
    {
        var settings = configuration.Settings.Copy();
        return SearchAsync(settings, DirectoryFilters.Computers(query, masks), r =>
            new AdComputer(Get(r, "name")!, Get(r, "dNSHostName"), r.DistinguishedName), token);
    }
    public Task<IReadOnlyList<AdComputer>> GetSearchComputersAsync(CancellationToken token) => FindComputersAsync("", token);
    public Task<IReadOnlyList<AdUser>> FindUsersAsync(string query, CancellationToken token) =>
        SearchAsync(configuration.Settings.Copy(), DirectoryFilters.Users(query), r =>
            new AdUser(Get(r, "sAMAccountName")!, Get(r, "userPrincipalName"), Get(r, "displayName") ?? Get(r, "sAMAccountName")!), token);

    private Task<IReadOnlyList<T>> SearchAsync<T>(UserSettings settings, string filter, Func<SearchResultEntry, T> map, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var password = configuration.GetPassword(settings);
        using var connection = CreateConnection(settings, password);
        token.ThrowIfCancellationRequested();
        var request = new SearchRequest(GetBaseDn(connection, settings), filter, SearchScope.Subtree,
            "name", "dNSHostName", "sAMAccountName", "userPrincipalName", "displayName");
        var paging = new PageResultRequestControl(500);
        request.Controls.Add(paging);
        var entries = new List<T>();
        do
        {
            token.ThrowIfCancellationRequested();
            var response = (SearchResponse)connection.SendRequest(request);
            token.ThrowIfCancellationRequested();
            entries.AddRange(response.Entries.Cast<SearchResultEntry>().Select(map));
            paging.Cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie ?? [];
        } while (paging.Cookie.Length > 0);
        return (IReadOnlyList<T>)entries;
    }, token);

    public Task<string> TestConnectionAsync(UserSettings settings, SecureString? password, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var connection = CreateConnection(settings, password);
        var baseDn = GetBaseDn(connection, settings);
        connection.SendRequest(new SearchRequest(baseDn, "(objectClass=*)", SearchScope.Base, "distinguishedName"));
        token.ThrowIfCancellationRequested();
        return "Соединение с AD установлено. Область поиска доступна.";
    }, token);

    private static LdapConnection CreateConnection(UserSettings settings, SecureString? password)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(string.IsNullOrWhiteSpace(settings.DomainController) ? null : settings.DomainController.Trim()));
        try
        {
            connection.AuthType = AuthType.Negotiate;
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.Signing = true;
            connection.SessionOptions.Sealing = true;
            connection.Timeout = TimeSpan.FromSeconds(15);
            if (settings.UseExplicitCredentials)
            {
                if (password is null || password.Length == 0) throw new InvalidOperationException("Введите пароль AD в настройках: сохранённый пароль отсутствует.");
                connection.Credential = DirectoryFilters.Credentials(settings.AdUserName, password);
            }
            connection.Bind();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    private static string GetBaseDn(LdapConnection connection, UserSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SearchBaseDn)) return settings.SearchBaseDn.Trim();
        var root = (SearchResponse)connection.SendRequest(new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "defaultNamingContext"));
        return Get(root.Entries[0], "defaultNamingContext") ?? throw new InvalidOperationException("Контроллер домена не вернул базу поиска.");
    }
    private static string? Get(SearchResultEntry entry, string name) => entry.Attributes[name]?.Count > 0 ? entry.Attributes[name][0]?.ToString() : null;
}

public static class DirectoryErrors
{
    // Never display server diagnostics or credential-containing exceptions.
    public static string Describe(Exception error) => error switch
    {
        LdapException { ErrorCode: 49 } => "AD отклонил учётные данные. Проверьте логин и пароль.",
        LdapException => "Не удалось связаться с AD. Проверьте контроллер, сеть и учётные данные.",
        DirectoryOperationException => "AD отклонил запрос. Проверьте права чтения и DN области поиска.",
        InvalidOperationException => error.Message,
        UnauthorizedAccessException => "Недостаточно прав для сохранения настроек.",
        IOException => "Не удалось сохранить настройки в профиле пользователя.",
        _ => "Не удалось выполнить операцию. Проверьте настройки подключения."
    };
}
