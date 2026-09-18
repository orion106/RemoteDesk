using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteAssist;

public interface ISystemAdapter
{
    LabOs Os { get; }
    Task<MachineSnapshot> ProbeAsync(LabComputer pc, OsEndpoint endpoint, CancellationToken token);
    Task<RemoteReply> RunAsync(LabComputer pc, OsEndpoint endpoint, string operation, object payload, string requestId, CancellationToken token);
    Task<RemoteReply?> ReconcileAsync(OsEndpoint endpoint, string requestId, CancellationToken token);
}
public static class LabWire
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public static string Script(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RemoteAssist.Scripts." + name) ?? throw new InvalidOperationException("Не найден встроенный сценарий: " + name);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public static string Request(LabComputer pc, string operation, object payload, LabOptions options) => JsonSerializer.Serialize(new { Operation = operation, ExpectedUuid = MachineIdentity.Valid(pc.Uuid) ? pc.Uuid : "", ExpectedSerial = MachineIdentity.Valid(pc.Serial) ? pc.Serial : "", Payload = payload, Options = options }, Json);
    public static void ValidateId(string id) { if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new InvalidOperationException("Некорректный идентификатор задания."); }
    public static void ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || !Regex.IsMatch(host, "^[a-zA-Z0-9_.:-]+$") || host.StartsWith('-')) throw new InvalidOperationException("Некорректное имя или адрес компьютера.");
    }
    public static string Sha(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    public static RemoteReply Parse(string json) => JsonSerializer.Deserialize<RemoteReply>(json, Json) ?? throw new InvalidDataException("Нет результата удалённой команды.");
    public static TimeSpan Timeout(string operation) => operation == "Install" ? TimeSpan.FromHours(2) : operation is "Power" or "RestartApplication" ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(2);
}

public sealed class WindowsLabAdapter(DirectoryConfiguration configuration) : ISystemAdapter
{
    public LabOs Os => LabOs.Windows;
    public async Task<MachineSnapshot> ProbeAsync(LabComputer pc, OsEndpoint endpoint, CancellationToken token)
    {
        var reply = await RunAsync(pc, endpoint, "Probe", new { }, Guid.NewGuid().ToString("N"), token);
        if (reply.Result.State != JobState.Succeeded) throw new InvalidOperationException(reply.Detail);
        var snapshot = reply.Data.Deserialize<MachineSnapshot>(LabWire.Json) ?? throw new InvalidDataException("Нет состояния Windows."); snapshot.Address = endpoint.Address; return snapshot;
    }
    internal static ManagementScope Scope(string host)
    {
        LabWire.ValidateHost(host);
        var scope = new ManagementScope($@"\\{host}\root\cimv2", new ConnectionOptions { Authentication = AuthenticationLevel.PacketPrivacy, Impersonation = ImpersonationLevel.Impersonate, EnablePrivileges = true, Timeout = TimeSpan.FromSeconds(15) }); scope.Connect(); return scope;
    }
    private static string Folder(string host, string id) { LabWire.ValidateHost(host); LabWire.ValidateId(id); return $@"\\{host}\C$\ProgramData\RemoteDesk\Jobs\{id}"; }
    public async Task<RemoteReply> RunAsync(LabComputer pc, OsEndpoint endpoint, string operation, object payload, string requestId, CancellationToken token)
    {
        LabWire.ValidateId(requestId); LabWire.ValidateHost(endpoint.Address);
        using var identity = new UpdateNetworkIdentity(configuration);
        var options = configuration.Settings.Lab.Copy();
        return await Task.Run(async () =>
        {
            var folder = Folder(endpoint.Address, requestId); var local = $@"C:\ProgramData\RemoteDesk\Jobs\{requestId}";
            var resultPath = Path.Combine(folder, "result.json");
            var started = false;
            try
            {
                identity.Run(() =>
                {
                    if (Directory.Exists(folder)) throw new InvalidOperationException("Задание уже существует на ПК. Выполните сверку результата, не повторный запуск.");
                    token.ThrowIfCancellationRequested(); Directory.CreateDirectory(folder);
                    var security = new System.Security.AccessControl.DirectorySecurity();
                    security.SetAccessRuleProtection(true, false);
                    foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) security.AddAccessRule(new(new System.Security.Principal.SecurityIdentifier(sid), System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                    new DirectoryInfo(folder).SetAccessControl(security);
                    File.WriteAllText(Path.Combine(folder, "request.json"), LabWire.Request(pc, operation, payload, options), new UTF8Encoding(true));
                    File.WriteAllText(Path.Combine(folder, "run.ps1"), LabWire.Script("WindowsAdmin.ps1"), new UTF8Encoding(true));
                    if (operation == "Install")
                    {
                        var package = ((PackagePayload)payload).Package;
                        using var source = new FileStream(package.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (!Convert.ToHexString(SHA256.HashData(source)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Исходный пакет изменился после выбора.");
                        source.Position = 0; using var target = new FileStream(Path.Combine(folder, "package." + package.Kind.ToLowerInvariant()), FileMode.CreateNew, FileAccess.Write, FileShare.None); source.CopyTo(target);
                    }
                    using var cls = new ManagementClass(Scope(endpoint.Address), new ManagementPath("Win32_Process"), null);
                    using var input = cls.GetMethodParameters("Create");
                    input["CommandLine"] = $"powershell.exe -NoProfile -NonInteractive -File \"{local}\\run.ps1\" -RequestPath \"{local}\\request.json\"";
                    using var startupClass = new ManagementClass(Scope(endpoint.Address), new ManagementPath("Win32_ProcessStartup"), null); using var startup = startupClass.CreateInstance();
                    if (startup is not null) { startup["ShowWindow"] = 0; input["ProcessStartupInformation"] = startup; }
                    token.ThrowIfCancellationRequested(); started = true;
                    using var response = cls.InvokeMethod("Create", input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(15) });
                    if (response is null || Convert.ToUInt32(response["ReturnValue"]) != 0) { started = false; throw new InvalidOperationException("WMI не запустил сценарий. Проверьте права и политику PowerShell."); }
                    return true;
                });
                var until = DateTimeOffset.UtcNow + LabWire.Timeout(operation);
                while (DateTimeOffset.UtcNow < until)
                {
                    var json = identity.Run(() => File.Exists(resultPath) ? File.ReadAllText(resultPath) : null);
                    if (json is not null)
                    {
                        var reply = LabWire.Parse(json);
                        if (operation is "Probe" or "Profiles") identity.Run(() => { try { foreach (var file in new[] { "run.ps1", "request.json", "result.json" }) File.Delete(Path.Combine(folder, file)); Directory.Delete(folder, false); } catch (IOException) { } catch (UnauthorizedAccessException) { } return true; });
                        return reply;
                    }
                    if (token.IsCancellationRequested) { identity.Run(() => { File.WriteAllText(Path.Combine(folder, "cancel.flag"), "cancel"); return true; }); return new() { State = "Uncertain", Detail = "Отмена отправлена. Начатую установку не прерываем; выполните сверку результата." }; }
                    await Task.Delay(750, CancellationToken.None).ConfigureAwait(false);
                }
                return new() { State = "Uncertain", Detail = "Истекло время ожидания. Результат команды требуется сверить; повторный запуск запрещён." };
            }
            catch (Exception ex) when (started && ex is not OutOfMemoryException)
            { return new() { State = "Uncertain", Detail = "Связь потеряна после отправки команды: " + ex.GetType().Name + ". Выполните сверку." }; }
        }, token);
    }
    public async Task<RemoteReply?> ReconcileAsync(OsEndpoint endpoint, string requestId, CancellationToken token)
    {
        using var identity = new UpdateNetworkIdentity(configuration);
        return await Task.Run(() => identity.Run(() => { var path = Path.Combine(Folder(endpoint.Address, requestId), "result.json"); return File.Exists(path) ? LabWire.Parse(File.ReadAllText(path)) : null; }), token);
    }
}

public sealed record PackagePayload(SoftwarePackage Package);
public sealed record PowerPayload(string Action, string WindowsMenuId, string BootConfigHash, bool BootPilotVerified);
public sealed record CleanupPayload(StudentProfile[] Profiles);
public sealed record RestartPayload(AllowedApplication Application, LabProcess Process);

public sealed class AstraLabAdapter(DirectoryConfiguration configuration, LabSecrets secrets) : ISystemAdapter
{
    public LabOs Os => LabOs.Astra;
    public ConnectionInfo Connection(OsEndpoint endpoint, LabOptions options)
    {
        LabWire.ValidateHost(endpoint.Address);
        if (options.SshUser.Length == 0) throw new InvalidOperationException("Укажите пользователя SSH.");
        var password = secrets.Read("ssh", options.SshUser);
        AuthenticationMethod method = options.SshPrivateKey.Length > 0
            ? new PrivateKeyAuthenticationMethod(options.SshUser, new PrivateKeyFile(options.SshPrivateKey, password))
            : new PasswordAuthenticationMethod(options.SshUser, password);
        return new(endpoint.Address, options.SshPort, options.SshUser, method) { Timeout = TimeSpan.FromSeconds(15) };
    }
    public static string Fingerprint(byte[] key) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
    public static void Trust(BaseClient client, OsEndpoint endpoint)
    {
        client.HostKeyReceived += (_, e) =>
        {
            var actual = Fingerprint(e.HostKey);
            e.CanTrust = endpoint.SshFingerprint.Length > 0 && actual == endpoint.SshFingerprint;
            if (!e.CanTrust) throw new SshConnectionException("Ключ SSH не подтверждён или изменился. Получен " + actual + ". Сверьте отпечаток на ПК и укажите его в карточке.");
        };
    }
    public SshClient CreateSsh(OsEndpoint endpoint)
    { var client = new SshClient(Connection(endpoint, configuration.Settings.Lab)); Trust(client, endpoint); return client; }
    public async Task<MachineSnapshot> ProbeAsync(LabComputer pc, OsEndpoint endpoint, CancellationToken token)
    {
        var reply = await RunAsync(pc, endpoint, "Probe", new { }, Guid.NewGuid().ToString("N"), token);
        if (reply.Result.State != JobState.Succeeded) throw new InvalidOperationException(reply.Detail);
        var snapshot = reply.Data.Deserialize<MachineSnapshot>(LabWire.Json) ?? throw new InvalidDataException("Нет состояния Astra."); snapshot.Address = endpoint.Address; return snapshot;
    }
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    public async Task<RemoteReply> RunAsync(LabComputer pc, OsEndpoint endpoint, string operation, object payload, string requestId, CancellationToken token)
    {
        LabWire.ValidateId(requestId);
        var options = configuration.Settings.Lab.Copy();
        return await Task.Run(async () =>
        {
            using var ssh = new SshClient(Connection(endpoint, options)); Trust(ssh, endpoint); ssh.Connect();
            using var sftp = new SftpClient(Connection(endpoint, options)); Trust(sftp, endpoint); sftp.Connect();
            var basePath = sftp.WorkingDirectory.TrimEnd('/') + "/.remote-desk";
            var folder = basePath + "/" + requestId;
            var started = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!sftp.Exists(basePath)) { sftp.CreateDirectory(basePath); sftp.ChangePermissions(basePath, 448); }
                if (sftp.GetAttributes(basePath).IsSymbolicLink) throw new InvalidOperationException("Каталог заданий SSH является ссылкой.");
                if (sftp.Exists(folder)) throw new InvalidOperationException("Задание уже существует на ПК: выполните сверку.");
                sftp.CreateDirectory(folder); sftp.ChangePermissions(folder, 448);
                UploadText(sftp, folder + "/run.py", LabWire.Script("AstraAdmin.py"));
                UploadText(sftp, folder + "/request.json", LabWire.Request(pc, operation, payload, options));
                if (operation == "Install" && payload is PackagePayload { Package.Kind: not "APT" } packagePayload)
                {
                    var package = packagePayload.Package; using var source = new FileStream(package.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (!Convert.ToHexString(SHA256.HashData(source)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Исходный пакет изменился после выбора.");
                    source.Position = 0; sftp.UploadFile(source, folder + "/package.deb");
                }
                using var command = ssh.CreateCommand("sudo -n python3 " + Quote(folder + "/run.py") + " " + Quote(folder + "/request.json"));
                command.CommandTimeout = LabWire.Timeout(operation);
                token.ThrowIfCancellationRequested(); started = true;
                // The script starts a detached worker and records its result. SSH cancellation never kills apt/dpkg.
                await command.ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
                if (command.ExitStatus != 0) return new() { State = "Failed", Detail = "Не удалось запустить сценарий Astra: проверьте python3 и разрешение sudo -n. " + command.Error.Trim() };
                var until = DateTimeOffset.UtcNow + LabWire.Timeout(operation);
                while (DateTimeOffset.UtcNow < until)
                {
                    if (sftp.Exists(folder + "/result.json"))
                    {
                        var reply = LabWire.Parse(sftp.ReadAllText(folder + "/result.json"));
                        if (operation == "Probe") { try { foreach (var file in new[] { "run.py", "request.json", "result.json" }) sftp.DeleteFile(folder + "/" + file); sftp.DeleteDirectory(folder); } catch (SshException) { } }
                        return reply;
                    }
                    if (token.IsCancellationRequested) { UploadText(sftp, folder + "/cancel.flag", "cancel"); return new() { State = "Uncertain", Detail = "Отмена отправлена. Сверьте результат начатой команды." }; }
                    await Task.Delay(750, CancellationToken.None).ConfigureAwait(false);
                }
                return new() { State = "Uncertain", Detail = "Время ожидания истекло; выполните сверку результата." };
            }
            catch (Exception ex) when (started && ex is not OutOfMemoryException) { return new() { State = "Uncertain", Detail = "Связь с Astra потеряна после запуска: " + ex.GetType().Name + ". Выполните сверку." }; }
        }, token);
    }
    private static void UploadText(SftpClient client, string path, string text) { using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(text)); client.UploadFile(bytes, path); client.ChangePermissions(path, 384); }
    public async Task<RemoteReply?> ReconcileAsync(OsEndpoint endpoint, string requestId, CancellationToken token)
    {
        LabWire.ValidateId(requestId);
        return await Task.Run(() => { using var sftp = new SftpClient(Connection(endpoint, configuration.Settings.Lab)); Trust(sftp, endpoint); sftp.Connect(); var path = sftp.WorkingDirectory.TrimEnd('/') + "/.remote-desk/" + requestId + "/result.json"; return sftp.Exists(path) ? LabWire.Parse(sftp.ReadAllText(path)) : null; }, token);
    }
}

public static class WakeOnLan
{
    public static byte[] Packet(string mac)
    {
        var hex = mac.Replace(":", "").Replace("-", "").Replace(".", "");
        if (!Regex.IsMatch(hex, "^[0-9a-fA-F]{12}$")) throw new InvalidOperationException("Некорректный MAC-адрес.");
        var bytes = Convert.FromHexString(hex); if ((bytes[0] & 1) != 0 || bytes.All(x => x == 0)) throw new InvalidOperationException("Нужен MAC физического сетевого адаптера.");
        var packet = new byte[102]; Array.Fill(packet, (byte)255, 0, 6); for (var i = 0; i < 16; i++) bytes.CopyTo(packet, 6 + i * 6); return packet;
    }
    public static async Task SendAsync(LabComputer pc, CancellationToken token)
    {
        if (pc.MacAddresses.Count == 0) throw new InvalidOperationException("Укажите MAC-адрес компьютера.");
        if (!IPAddress.TryParse(pc.Broadcast, out var address) || address.AddressFamily != AddressFamily.InterNetwork) throw new InvalidOperationException("Укажите IPv4 broadcast нужной подсети.");
        using var udp = new UdpClient { EnableBroadcast = true };
        foreach (var mac in pc.MacAddresses) await udp.SendAsync(Packet(mac), new IPEndPoint(address, 9), token);
    }
}
