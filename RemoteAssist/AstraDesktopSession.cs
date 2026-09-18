using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Renci.SshNet;

namespace RemoteAssist;

public sealed class AstraDesktopSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SshClient _ssh;
    private readonly SftpClient _sftp;
    private readonly ForwardedPortLocal _tunnel;
    private readonly Process _viewer;
    private readonly string _remote, _local;
    private readonly Task _watch;
    private int _disposed;
    private AstraDesktopSession(SshClient ssh, SftpClient sftp, ForwardedPortLocal tunnel, Process viewer, string remote, string local)
    { _ssh = ssh; _sftp = sftp; _tunnel = tunnel; _viewer = viewer; _remote = remote; _local = local; _watch = WatchAsync(); }
    public static byte[] PasswordFile(byte[] password)
    {
        // VNC password-file format (d3des uses bit-reversed DES key bytes).
        byte Reverse(byte b) { byte r = 0; for (var i = 0; i < 8; i++) { r = (byte)((r << 1) | (b & 1)); b >>= 1; } return r; }
        using var des = DES.Create(); des.Mode = CipherMode.ECB; des.Padding = PaddingMode.None;
        des.Key = new byte[] { 23, 82, 107, 6, 35, 78, 88, 7 }.Select(Reverse).ToArray();
        using var encrypt = des.CreateEncryptor(); return encrypt.TransformFinalBlock(password, 0, 8);
    }
    public static async Task<AstraDesktopSession> StartAsync(AstraLabAdapter adapter, LabOptions options, LabComputer pc, OsEndpoint endpoint, CancellationToken token)
    {
        if (!File.Exists(options.ViewerPath) || !Path.GetFileName(options.ViewerPath).Contains("vncviewer", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Укажите установленный TigerVNC Viewer (vncviewer.exe) в настройках.");
        var id = Guid.NewGuid().ToString("N");
        var password = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(4)));
        var encoded = PasswordFile(password); CryptographicOperations.ZeroMemory(password);
        var reply = await adapter.RunAsync(pc, endpoint, "VncStart", new { PasswordFile = Convert.ToBase64String(encoded) }, id, token);
        if (reply.Result.State != JobState.Succeeded) throw new InvalidOperationException(reply.Detail);
        var port = reply.Data.GetProperty("Port").GetInt32();
        if (port is < 1024 or > 65535) throw new InvalidDataException("Некорректный порт VNC.");
        SshClient? ssh = null; SftpClient? sftp = null; ForwardedPortLocal? tunnel = null;
        var local = Path.Combine(Path.GetTempPath(), "RemoteDesk-vnc-" + id); var remote = "";
        try
        {
            Directory.CreateDirectory(local);
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new(new SecurityIdentifier("S-1-5-18"), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(local).SetAccessControl(acl);
            var passwordPath = Path.Combine(local, "passwd"); File.WriteAllBytes(passwordPath, encoded); CryptographicOperations.ZeroMemory(encoded);
            ssh = adapter.CreateSsh(endpoint); await ssh.ConnectAsync(token);
            sftp = new SftpClient(adapter.Connection(endpoint, options)); AstraLabAdapter.Trust(sftp, endpoint); await sftp.ConnectAsync(token);
            remote = sftp.WorkingDirectory.TrimEnd('/') + "/.remote-desk/" + id;
            tunnel = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)port); ssh.AddForwardedPort(tunnel); tunnel.Start();
            var start = new ProcessStartInfo(options.ViewerPath) { UseShellExecute = false };
            start.ArgumentList.Add("-PasswordFile"); start.ArgumentList.Add(passwordPath); start.ArgumentList.Add("-ReconnectOnError=0"); start.ArgumentList.Add("127.0.0.1::" + tunnel.BoundPort);
            var viewer = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить TigerVNC.");
            return new(ssh, sftp, tunnel, viewer, remote, local);
        }
        catch
        {
            if (sftp?.IsConnected == true && remote.Length > 0) try { sftp.WriteAllText(remote + "/vnc.stop", "stop"); } catch { }
            tunnel?.Dispose(); ssh?.Dispose(); sftp?.Dispose();
            RemoveLocal(local); throw;
        }
    }
    private async Task WatchAsync()
    {
        try
        {
            while (!_viewer.HasExited && !_stop.IsCancellationRequested && _ssh.IsConnected && _sftp.IsConnected)
            {
                await Task.Run(() => _sftp.WriteAllText(_remote + "/vnc.lease", DateTimeOffset.UtcNow.ToString("O")));
                await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            try { if (_sftp.IsConnected) await Task.Run(() => _sftp.WriteAllText(_remote + "/vnc.stop", "stop")); } catch { }
            try { if (!_viewer.HasExited) _viewer.Kill(); } catch { }
            _tunnel.Dispose(); _ssh.Dispose(); _sftp.Dispose(); _viewer.Dispose(); RemoveLocal(_local);
        }
    }
    private static void RemoveLocal(string local)
    {
        var full = Path.GetFullPath(local); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("RemoteDesk-vnc-", StringComparison.Ordinal)) return;
        try { File.Delete(Path.Combine(full, "passwd")); Directory.Delete(full, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _stop.Cancel(); await _watch; _stop.Dispose(); }
}
