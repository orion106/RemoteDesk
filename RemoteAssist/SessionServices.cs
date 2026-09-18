using System.Runtime.InteropServices;
using System.Text;

namespace RemoteAssist;

public sealed class SessionService : ISessionService
{
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<UserSession> Data)> _cache = new(StringComparer.OrdinalIgnoreCase);
    public void ClearCache() => _cache.Clear();

    public async Task<IReadOnlyList<UserSession>> GetSessionsAsync(string computer, CancellationToken token, bool force = false)
    {
        token.ThrowIfCancellationRequested();
        if (!force && _cache.TryGetValue(computer, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(60)) return cached.Data;
        try
        {
            var output = await RunQueryAsync(computer, token).ConfigureAwait(false);
            var sessions = QuerySessionParser.Parse(output, computer).Where(s => !string.IsNullOrWhiteSpace(s.UserName)).Select(Enrich).ToArray();
            token.ThrowIfCancellationRequested();
            if (sessions.Length == 0) sessions = [new UserSession(computer, null, null, -1, "Нет сеансов", DateTime.Now, null)];
            _cache[computer] = (DateTime.UtcNow, sessions);
            return sessions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [new UserSession(computer, null, null, -1, "Ошибка", DateTime.Now, ex.Message)];
        }
    }
    private static async Task<string> RunQueryAsync(string computer, CancellationToken token)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "query.exe"))
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.GetEncoding((int)GetOEMCP()), StandardErrorEncoding = Encoding.GetEncoding((int)GetOEMCP())
            }
        };
        process.StartInfo.ArgumentList.Add("session");
        process.StartInfo.ArgumentList.Add($"/server:{computer}");
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("Компьютер не ответил на запрос сеансов за 15 секунд.");
        }
        var result = await output.ConfigureAwait(false);
        var diagnostic = await error.ConfigureAwait(false);
        if (process.ExitCode != 0 && QuerySessionParser.Parse(result, computer).Count == 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(diagnostic) ? "Не удалось получить сеансы. Проверьте права и доступность ПК." : diagnostic.Trim());
        return result;
    }
    private static UserSession Enrich(UserSession session)
    {
        try
        {
            var user = Wts.Get(session.Computer, session.Id, 5);
            var domain = Wts.Get(session.Computer, session.Id, 7);
            return string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(domain)
                ? session with { Error = "Не удалось подтвердить владельца сеанса через WTS." }
                : session with { UserName = user, Domain = domain };
        }
        catch { return session with { Error = "Не удалось подтвердить владельца сеанса через WTS." }; }
    }
    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();
}

public sealed class RemoteDesktopLauncher : IRemoteDesktopLauncher
{
    public void Launch(UserSession session)
    {
        if (!session.CanConnect) throw new InvalidOperationException("Этот сеанс недоступен для подключения.");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mstsc.exe")) { UseShellExecute = false };
        start.ArgumentList.Add($"/v:{session.Computer}");
        start.ArgumentList.Add($"/shadow:{session.Id}");
        start.ArgumentList.Add("/control");
        Process.Start(start)?.Dispose();
    }
}

internal static class Wts
{
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr WTSOpenServer(string serverName);
    [DllImport("wtsapi32.dll")] private static extern void WTSCloseServer(IntPtr server);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int size);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    public static string? Get(string computer, int id, int infoClass)
    {
        var server = WTSOpenServer(computer);
        if (server == IntPtr.Zero) return null;
        try
        {
            if (!WTSQuerySessionInformation(server, id, infoClass, out var buffer, out _)) return null;
            try { return Marshal.PtrToStringUni(buffer); }
            finally { WTSFreeMemory(buffer); }
        }
        finally { WTSCloseServer(server); }
    }
}
