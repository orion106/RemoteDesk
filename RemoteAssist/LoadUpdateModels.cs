using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace RemoteAssist;

public enum LoadUpdateState { Found, NotInstalled, Busy, Updated, Current, Declined, TimedOut, Deferred, AccessDenied, Offline, Failed, Uncertain, Cancelled }

public sealed record LoadInstallation(string Computer, string Path, string Version,
    LoadUpdateState State = LoadUpdateState.Found, string Detail = "");

public sealed record LoadUpdateResult(LoadUpdateState State, string Detail = "", string? BackupPath = null)
{
    public string Label => State switch
    {
        LoadUpdateState.Found => "Найдена", LoadUpdateState.NotInstalled => "Не установлена",
        LoadUpdateState.Busy => "Занят", LoadUpdateState.Updated => "Обновлён",
        LoadUpdateState.Current => "Уже обновлён", LoadUpdateState.Declined => "Пользователь отказал",
        LoadUpdateState.TimedOut => "Нет ответа", LoadUpdateState.Deferred => "Отложено",
        LoadUpdateState.AccessDenied => "Нет доступа", LoadUpdateState.Offline => "ПК недоступен",
        LoadUpdateState.Uncertain => "Результат не подтверждён", LoadUpdateState.Cancelled => "Отменено", _ => "Ошибка"
    };
}

public sealed class LoadUpdateRow(LoadInstallation installation) : ObservableObject
{
    private bool _selected = installation.State == LoadUpdateState.Found;
    private string _newVersion = "—", _version = installation.Version;
    private LoadUpdateResult _result = new(installation.State, installation.Detail);
    public LoadInstallation Installation { get; } = installation;
    public string Computer => Installation.Computer;
    public string Path => Installation.Path;
    public string Version => _version;
    public string NewVersion { get => _newVersion; set => Set(ref _newVersion, value); }
    public bool CanSelect => !string.IsNullOrEmpty(Path) && Installation.State == LoadUpdateState.Found;
    public bool Selected { get => _selected; set => Set(ref _selected, CanSelect && value); }
    public LoadUpdateResult Result => _result;
    public string Status => _result.Label;
    public string Detail => string.IsNullOrEmpty(_result.BackupPath) ? _result.Detail : $"{_result.Detail} Резервная копия: {_result.BackupPath}";
    public void Apply(LoadUpdateResult result, string? installedVersion = null)
    {
        _result = result;
        if (installedVersion is not null) _version = installedVersion;
        OnPropertyChanged(nameof(Version)); OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Detail));
    }
}

// A private, read-locked snapshot prevents a changed source file from changing an in-flight update.
public sealed class LoadUpdatePackage : IDisposable
{
    private readonly FileStream _lease;
    public string SnapshotPath { get; }
    public string SourcePath { get; }
    public string Version { get; }
    public string Hash { get; }
    private LoadUpdatePackage(string source, string snapshot, FileStream lease, string hash)
    {
        SourcePath = source; SnapshotPath = snapshot; _lease = lease; Hash = hash;
        Version = FileVersionInfo.GetVersionInfo(snapshot).FileVersion ?? "Не указана";
    }
    public static LoadUpdatePackage Create(string source, CancellationToken token)
    {
        if (!System.IO.Path.GetFileName(source).Equals("Service.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выберите исполняемый файл с именем Service.exe.");
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAssist", "UpdatePackages");
        Directory.CreateDirectory(folder);
        var snapshot = System.IO.Path.Combine(folder, Guid.NewGuid().ToString("N") + ".exe");
        FileStream? lease = null;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (input.Length == 0) throw new InvalidOperationException("Выбранный файл пуст.");
                var buffer = new byte[128 * 1024];
                int count;
                while ((count = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                output.Flush(true);
            }
            lease = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read);
            using (var pe = new PEReader(lease, PEStreamOptions.LeaveOpen))
            {
                var headers = pe.PEHeaders;
                if (headers.PEHeader is null || !headers.IsExe || headers.SectionHeaders.Length == 0 ||
                    headers.SectionHeaders.Any(s => s.PointerToRawData < 0 || s.SizeOfRawData < 0 || (long)s.PointerToRawData + s.SizeOfRawData > lease.Length))
                    throw new InvalidOperationException("Service.exe не является корректным исполняемым файлом Windows.");
            }
            lease.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(lease));
            token.ThrowIfCancellationRequested();
            return new LoadUpdatePackage(source, snapshot, lease, hash);
        }
        catch
        {
            lease?.Dispose();
            try { File.Delete(snapshot); } catch (IOException) { }
            throw;
        }
    }
    public void Dispose()
    {
        _lease.Dispose();
        try { File.Delete(SnapshotPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public interface IRemoteUpdateSessionFactory { IRemoteUpdateSession Open(DirectoryConfiguration configuration); }
public interface IRemoteUpdateSession : IDisposable
{
    Task<IReadOnlyList<LoadInstallation>> ScanAsync(string computer, CancellationToken token);
    Task<LoadUpdateResult> UpdateAsync(LoadInstallation installation, LoadUpdatePackage package, bool requestConsent, CancellationToken token);
}

public static class LoadUpdateErrors
{
    public static LoadUpdateResult Describe(Exception error)
    {
        var code = error is Win32Exception native ? native.NativeErrorCode : error.HResult & 0xffff;
        return error switch
        {
            OperationCanceledException => new(LoadUpdateState.Cancelled, "Операция отменена."),
            UnauthorizedAccessException => new(LoadUpdateState.AccessDenied, "Недостаточно прав. Проверьте учётную запись и доступ к C$/WMI/WTS."),
            System.Management.ManagementException management when management.ErrorCode is System.Management.ManagementStatus.AccessDenied or System.Management.ManagementStatus.PrivilegeNotHeld =>
                new(LoadUpdateState.AccessDenied, "WMI отклонил операцию. Проверьте права удалённого доступа и управления процессами."),
            _ when code is 5 or 1314 or 1326 or 1219 => new(LoadUpdateState.AccessDenied, $"Ошибка доступа Windows {code}. Проверьте учётную запись и сетевые подключения."),
            IOException when code is 32 or 33 => new(LoadUpdateState.Busy, "Файл используется. После основного обновления можно запросить согласие пользователя."),
            _ when code is 53 or 64 or 67 or 121 or 1231 or 1722 or 1726 or 1460 => new(LoadUpdateState.Offline, $"Сеть или удалённая служба недоступна (Windows {code})."),
            TimeoutException => new(LoadUpdateState.Offline, "Удалённый компьютер не ответил вовремя."),
            InvalidOperationException => new(LoadUpdateState.Failed, error.Message),
            _ => new(LoadUpdateState.Failed, $"Операция не выполнена ({error.GetType().Name}, код 0x{error.HResult:X8}).")
        };
    }
}

public interface IUpdateJournal { void Write(object entry); }
public sealed class UpdateJournal : IUpdateJournal
{
    private static readonly object Sync = new();
    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAssist", "UpdateLogs");
    public void Write(object entry)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Folder);
            using var stream = new FileStream(System.IO.Path.Combine(Folder, $"{DateTime.Now:yyyy-MM-dd}.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
            var data = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Entry = entry }) + Environment.NewLine);
            stream.Write(data); stream.Flush(true);
        }
    }
}
