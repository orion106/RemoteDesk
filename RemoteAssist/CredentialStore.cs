using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace RemoteAssist;

public interface ICredentialStore
{
    SecureString? Read(string target);
    void Write(string target, string userName, SecureString password);
    void Delete(string target);
}

public sealed class WindowsCredentialStore : ICredentialStore
{
    public static string Target(UserSettings settings)
    {
        var identity = $"{settings.DomainController?.Trim().ToUpperInvariant() ?? "AUTO"}\n{settings.AdUserName.Trim().ToUpperInvariant()}";
        return "RemoteAssist/AD/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public SecureString? Read(string target)
    {
        if (!CredReadW(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new InvalidOperationException("Windows не удалось прочитать сохранённый пароль.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var password = new SecureString();
            for (var i = 0; i < credential.CredentialBlobSize / 2; i++)
                password.AppendChar((char)Marshal.ReadInt16(credential.CredentialBlob, i * 2));
            password.MakeReadOnly();
            return password;
        }
        finally { CredFree(pointer); }
    }

    public void Write(string target, string userName, SecureString password)
    {
        if (password.Length * 2 > 2560) throw new InvalidOperationException("Пароль слишком длинный для хранилища Windows.");
        var pointer = Marshal.SecureStringToCoTaskMemUnicode(password);
        try
        {
            var credential = new Credential
            {
                Type = 1, TargetName = target, UserName = userName, CredentialBlob = pointer,
                CredentialBlobSize = password.Length * 2, Persist = 2,
                Comment = "RemoteAssist: Active Directory и обновление нагрузки"
            };
            if (!CredWriteW(ref credential, 0)) throw new InvalidOperationException("Windows не удалось сохранить пароль.");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
    }

    public void Delete(string target)
    {
        if (!CredDeleteW(target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new InvalidOperationException("Windows не удалось удалить сохранённый пароль.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CredWriteW(ref Credential credential, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);
    [DllImport("advapi32.dll", ExactSpelling = true)] private static extern void CredFree(IntPtr buffer);
}

public sealed class DirectoryConfiguration(UserSettings settings, ICredentialStore store, string? settingsPath = null) : IDisposable
{
    private SecureString? _sessionPassword;
    private readonly object _sync = new();
    public UserSettings Settings { get; private set; } = settings.Copy();
    public event Action? Changed;

    public SecureString? GetPassword(UserSettings settings)
    {
        if (!settings.UseExplicitCredentials) return null;
        lock (_sync)
        {
            var sameAccount = WindowsCredentialStore.Target(settings) == WindowsCredentialStore.Target(Settings);
            if (sameAccount && _sessionPassword is not null) return _sessionPassword.Copy();
            return settings.RememberPassword || (sameAccount && Settings.RememberPassword)
                ? store.Read(WindowsCredentialStore.Target(settings)) : null;
        }
    }

    public void Save(UserSettings candidate, SecureString? enteredPassword, bool removeStoredPassword)
    {
        candidate = candidate.Copy();
        candidate.ComputerMasks = DirectoryFilters.ParseMasks(string.Join('\n', candidate.ComputerMasks));
        candidate.LoadUpdateMasks = candidate.LoadUpdateMasks.Length == 0 ? [] : DirectoryFilters.ParseMasks(string.Join('\n', candidate.LoadUpdateMasks));
        using var password = !candidate.UseExplicitCredentials || removeStoredPassword ? null :
            enteredPassword is { Length: > 0 } ? enteredPassword.Copy() : GetPassword(candidate);
        if (candidate.UseExplicitCredentials && !removeStoredPassword)
        {
            if (password is null || password.Length == 0) throw new InvalidOperationException("Введите пароль учётной записи AD.");
            DirectoryFilters.Credentials(candidate.AdUserName, password);
        }
        if (removeStoredPassword) candidate.RememberPassword = false;
        var key = WindowsCredentialStore.Target(candidate);
        if (removeStoredPassword || !candidate.RememberPassword) store.Delete(key);
        else if (candidate.UseExplicitCredentials && password is not null) store.Write(key, candidate.AdUserName, password);
        candidate.Save(settingsPath);
        lock (_sync)
        {
            _sessionPassword?.Dispose();
            _sessionPassword = removeStoredPassword ? null : password?.Copy();
            Settings = candidate;
        }
        Changed?.Invoke();
    }

    public void Dispose() { lock (_sync) { _sessionPassword?.Dispose(); _sessionPassword = null; } }
}
