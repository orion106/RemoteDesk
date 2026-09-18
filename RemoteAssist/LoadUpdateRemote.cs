using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace RemoteAssist;

public sealed class RemoteUpdateSessionFactory : IRemoteUpdateSessionFactory
{
    public IRemoteUpdateSession Open(DirectoryConfiguration configuration) => new RemoteUpdateSession(new UpdateNetworkIdentity(configuration));
}

public sealed class UpdateNetworkIdentity : IDisposable
{
    private readonly SafeAccessTokenHandle? _token;
    public UpdateNetworkIdentity(DirectoryConfiguration configuration)
    {
        var settings = configuration.Settings.Copy();
        if (!settings.UseExplicitCredentials) return;
        using var password = configuration.GetPassword(settings);
        if (password is null || password.Length == 0) throw new InvalidOperationException("Введите пароль в настройках для обновления нагрузки.");
        var credentials = DirectoryFilters.Credentials(settings.AdUserName, password);
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            if (!LogonUserW(credentials.UserName, string.IsNullOrEmpty(credentials.Domain) ? null : credentials.Domain, pointer, 9, 3, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _token = token;
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }
    public T Run<T>(Func<T> operation) => _token is null ? operation() : WindowsIdentity.RunImpersonated(_token, operation);
    public void Dispose() => _token?.Dispose();
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool LogonUserW(string user, string? domain, IntPtr password, int type, int provider, out SafeAccessTokenHandle token);
}

internal sealed class RemoteUpdateSession(UpdateNetworkIdentity identity) : IRemoteUpdateSession
{
    private readonly PhysicalUpdateFiles _files = new();
    public Task<IReadOnlyList<LoadInstallation>> ScanAsync(string computer, CancellationToken token) =>
        Task.Run(() => identity.Run(() => new LoadInstallationScanner(_files).Scan(computer, token)), token);
    public Task<LoadUpdateResult> UpdateAsync(LoadInstallation installation, LoadUpdatePackage package, bool requestConsent, CancellationToken token) =>
        Task.Run(() => identity.Run(() => new LoadFileUpdater(_files, new UpdateJournal()).Update(installation, package, token,
            requestConsent ? () => new LoadConsentCoordinator(new WmiLoadProcesses()).CloseWithConsent(installation, token) : null)), token);
    public void Dispose() => identity.Dispose();
}

public sealed class WmiLoadProcesses : ILoadProcesses
{
    private static ManagementScope Connect(string computer)
    {
        _ = LoadUpdatePaths.Share(computer);
        var scope = new ManagementScope($@"\\{computer}\root\cimv2", new ConnectionOptions
        {
            Authentication = AuthenticationLevel.PacketPrivacy, Impersonation = ImpersonationLevel.Impersonate,
            EnablePrivileges = true, Timeout = TimeSpan.FromSeconds(15)
        });
        scope.Connect();
        return scope;
    }
    public IReadOnlyList<LoadProcess> Find(string computer, string path)
    {
        using var query = new ManagementObjectSearcher(Connect(computer), new ObjectQuery("SELECT * FROM Win32_Process WHERE Name='Service.exe'"),
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = true });
        using var collection = query.Get();
        var result = new List<LoadProcess>();
        foreach (ManagementObject item in collection)
        using (item)
        {
            var executable = item["ExecutablePath"] as string;
            if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("WMI не подтвердил путь Service.exe. Проверьте права администратора.");
            if (executable.Equals(path, StringComparison.OrdinalIgnoreCase)) result.Add(Read(item));
        }
        return result;
    }
    private static LoadProcess Read(ManagementObject item)
    {
        using var owner = item.InvokeMethod("GetOwner", null, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(15) });
        using var sid = item.InvokeMethod("GetOwnerSid", null, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(15) });
        if (owner is null || sid is null || Convert.ToUInt32(owner["ReturnValue"]) != 0 || Convert.ToUInt32(sid["ReturnValue"]) != 0)
            throw new InvalidOperationException("Не удалось подтвердить владельца процесса.");
        var process = new LoadProcess(Convert.ToUInt32(item["ProcessId"]), item["ExecutablePath"] as string ?? "",
            item["CreationDate"] as string ?? "", Convert.ToInt32(item["SessionId"]), $"{owner["Domain"]}\\{owner["User"]}", sid["Sid"] as string ?? "");
        if (string.IsNullOrWhiteSpace(process.Created) || string.IsNullOrWhiteSpace(process.OwnerSid) || string.IsNullOrWhiteSpace(owner["User"] as string))
            throw new InvalidOperationException("WMI вернул неполные сведения о процессе.");
        return process;
    }
    private static ManagementObject? Verified(ManagementScope scope, LoadProcess expected)
    {
        var item = new ManagementObject(scope, new ManagementPath($"Win32_Process.Handle='{expected.Id}'"), new ObjectGetOptions { Timeout = TimeSpan.FromSeconds(15) });
        try
        {
            item.Get();
            if (!expected.SameAs(Read(item))) throw new InvalidOperationException("Процесс изменился после согласия пользователя.");
            return item;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound) { item.Dispose(); return null; }
        catch { item.Dispose(); throw; }
    }
    public bool ConfirmSession(string computer, LoadProcess process) => WtsUpdateDialog.Confirm(computer, process);
    public UpdateConsent Ask(string computer, LoadProcess process) => WtsUpdateDialog.Ask(computer, process);
    public void RequestClose(string computer, LoadProcess process)
    {
        var scope = Connect(computer);
        using var verified = Verified(scope, process);
        if (verified is null) return;
        if (!ConfirmSession(computer, process)) throw new InvalidOperationException("Пользовательский сеанс изменился.");
        // Run taskkill locally on the target: /S would force termination even without /F.
        using var osQuery = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT SystemDirectory FROM Win32_OperatingSystem"),
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = true });
        using var systems = osQuery.Get();
        string? systemDirectory = null;
        foreach (ManagementObject system in systems) using (system) systemDirectory = system["SystemDirectory"] as string;
        if (string.IsNullOrWhiteSpace(systemDirectory) || systemDirectory.Contains('"')) throw new InvalidOperationException("Не удалось определить системный каталог Windows.");
        using var cls = new ManagementClass(scope, new ManagementPath("Win32_Process"), null);
        using var input = cls.GetMethodParameters("Create");
        if (process.Owner.Any(c => c == '"' || char.IsControl(c))) throw new InvalidOperationException("Не удалось подтвердить имя владельца процесса.");
        input["CommandLine"] = $"\"{systemDirectory}\\taskkill.exe\" /PID {process.Id} /FI \"IMAGENAME eq Service.exe\" /FI \"SESSION eq {process.SessionId}\" /FI \"USERNAME eq {process.Owner}\"";
        using var startupClass = new ManagementClass(scope, new ManagementPath("Win32_ProcessStartup"), null);
        using var startup = startupClass.CreateInstance();
        if (startup is not null) { startup["ShowWindow"] = 0; input["ProcessStartupInformation"] = startup; }
        using var result = cls.InvokeMethod("Create", input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(15) });
        // An unsuccessful ordinary close still gets the full grace period. Force termination
        // is independently revalidated and authorized by the user's explicit Yes.
        if (result is null) throw new InvalidOperationException("WMI не подтвердил ответ на запрос обычного закрытия.");
    }
    public void Terminate(string computer, LoadProcess process)
    {
        using var verified = Verified(Connect(computer), process);
        if (verified is null) return;
        if (!ConfirmSession(computer, process)) throw new InvalidOperationException("Пользовательский сеанс изменился.");
        using var input = verified.GetMethodParameters("Terminate"); input["Reason"] = 0;
        using var result = verified.InvokeMethod("Terminate", input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(15) });
        if (result is null || Convert.ToUInt32(result["ReturnValue"]) != 0) throw new InvalidOperationException("Не удалось завершить согласованный процесс нагрузки.");
    }
}

internal static class WtsUpdateDialog
{
    public static bool Confirm(string computer, LoadProcess process)
    {
        var server = WTSOpenServerW(computer);
        if (server == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return Confirm(server, process); }
        finally { WTSCloseServer(server); }
    }
    private static bool Confirm(IntPtr server, LoadProcess process)
    {
        var user = ReadString(server, process.SessionId, 5);
        var domain = ReadString(server, process.SessionId, 7);
        if (!WTSQuerySessionInformationW(server, process.SessionId, 8, out var buffer, out var size)) return false;
        try { return size >= 4 && Marshal.ReadInt32(buffer) == 0 && $"{domain}\\{user}".Equals(process.Owner, StringComparison.OrdinalIgnoreCase); }
        finally { WTSFreeMemory(buffer); }
    }
    private static string? ReadString(IntPtr server, int session, int info)
    {
        if (!WTSQuerySessionInformationW(server, session, info, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }
    public static UpdateConsent Ask(string computer, LoadProcess process)
    {
        var server = WTSOpenServerW(computer);
        if (server == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!Confirm(server, process)) return UpdateConsent.Unavailable;
            const string title = "Обновление нагрузки";
            // MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2. Native lengths are BYTES, not characters.
            if (!WTSSendMessageW(server, process.SessionId, title, Encoding.Unicode.GetByteCount(title),
                    LoadConsentCoordinator.Prompt, Encoding.Unicode.GetByteCount(LoadConsentCoordinator.Prompt), 0x134, 300, out var answer, true))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Confirm(server, process)) return UpdateConsent.Unavailable;
            return answer switch { 6 => UpdateConsent.Yes, 7 => UpdateConsent.No, 32000 => UpdateConsent.Timeout, _ => UpdateConsent.Unavailable };
        }
        finally { WTSCloseServer(server); }
    }
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] private static extern IntPtr WTSOpenServerW(string server);
    [DllImport("wtsapi32.dll")] private static extern void WTSCloseServer(IntPtr server);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int info, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSSendMessageW(IntPtr server, int session, string title, int titleLength, string message, int messageLength, int style, int timeout, out int response, [MarshalAs(UnmanagedType.Bool)] bool wait);
}
