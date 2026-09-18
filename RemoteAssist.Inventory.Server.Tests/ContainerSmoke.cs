using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteAssist.Inventory;
using RemoteAssist.Inventory.Server;

internal static class ContainerSmoke
{
    public static async Task Run()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remoteassist-container-smoke-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(scratch, "data"); var backups = Path.Combine(scratch, "backups"); var config = Path.Combine(data, "config"); var restored = Path.Combine(scratch, "restored");
        Directory.CreateDirectory(config); Directory.CreateDirectory(backups); Directory.CreateDirectory(restored);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); var tlsPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false)); request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
        File.WriteAllBytes(Path.Combine(config, "server.pfx"), certificate.Export(X509ContentType.Pfx, tlsPassword));
        File.WriteAllText(Path.Combine(config, "owner-password.txt"), password); File.WriteAllText(Path.Combine(config, "tls-password.txt"), tlsPassword);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var name = "inventory-smoke-" + Guid.NewGuid().ToString("N");
        var arguments = new[] { "run", "--rm", "-d", "--name", name, "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=128m", "-p", $"127.0.0.1:{port}:8443", "--mount", $"type=bind,source={data},target=/data", "--mount", $"type=bind,source={backups},target=/backups", "-e", "INVENTORY_OWNER_USERNAME=smoke-owner", "-e", "INVENTORY_OWNER_PASSWORD_FILE=/data/config/owner-password.txt", "-e", "INVENTORY_TLS_PASSWORD_FILE=/data/config/tls-password.txt", "-e", "Kestrel__Certificates__Default__Path=/data/config/server.pfx", (Environment.GetEnvironmentVariable("INVENTORY_TEST_IMAGE") ?? "remoteassist-inventory:1.1") };
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, received, _, _) => received is not null && received.Thumbprint == certificate.Thumbprint }; // Test-only pin of this generated certificate, never used by the application.
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };
        var running = false;
        async Task Start()
        {
            await Docker(arguments); running = true;
            for (var i = 0; i < 80; i++) { try { if ((await client.GetAsync("/api/health")).IsSuccessStatusCode) return; } catch (HttpRequestException) { } await Task.Delay(200); }
            throw new Exception("Container did not become healthy. " + await Docker(["logs", name]));
        }
        try
        {
            await Start();
            var settings = await Docker(["inspect", "--format", "{{.Config.User}} {{.HostConfig.ReadonlyRootfs}}", name]);
            Assert(settings.Trim() == "1654:1654 true", "Linux image runs non-root with read-only root filesystem");
            Assert((await client.GetAsync("/api/snapshot")).StatusCode == HttpStatusCode.Unauthorized, "HTTPS snapshot requires authentication");
            var loginResponse = await client.PostAsJsonAsync("/api/login", new LoginRequest { Username = "smoke-owner", Password = password }); loginResponse.EnsureSuccessStatusCode();
            var login = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!; client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
            var room = new RoomDto { Number = "301", Floor = 3, Name = "Docker smoke test" }; (await client.PostAsJsonAsync("/api/rooms", new SaveCommand<RoomDto> { Value = room })).EnsureSuccessStatusCode();
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1sAAAAASUVORK5CYII=");
            using var multipart = new MultipartFormDataContent(); multipart.Add(new StringContent(Guid.NewGuid().ToString("N")), "commandId"); multipart.Add(new StringContent("room"), "ownerType"); multipart.Add(new StringContent(room.Id), "ownerId"); multipart.Add(new ByteArrayContent(png), "file", "room.png");
            (await client.PostAsync("/api/photos/upload", multipart)).EnsureSuccessStatusCode();
            var snapshot = (await client.GetFromJsonAsync<InventorySnapshot>("/api/snapshot"))!;
            (await client.PostAsync("/api/backups", null)).EnsureSuccessStatusCode();
            Assert(snapshot.Rooms.Single().Id == room.Id && snapshot.Photos.Count == 1, "SQLite and managed photo writes work in Linux mounted datasets");
            Assert((await client.GetFromJsonAsync<BackupStatus>("/api/backups/status"))!.LastSuccessUtc.HasValue, "Linux server produces a verified backup");
            await Docker(["stop", "-t", "15", name]); running = false;
            await Start();
            var afterRestart = (await client.GetFromJsonAsync<InventorySnapshot>("/api/snapshot"))!;
            Assert(afterRestart.InstanceId == snapshot.InstanceId && afterRestart.Rooms.Single().Id == room.Id && afterRestart.Photos.Count == 1, "restart preserves database identity, records, photographs and sessions");
            var photoBytes = await client.GetByteArrayAsync($"/api/photos/{snapshot.Photos[0].Id}/content"); Assert(photoBytes.SequenceEqual(png), "saved photo remains readable after restart");
            var backup = Directory.EnumerateDirectories(backups, "inventory-*").OrderByDescending(Directory.GetCreationTimeUtc).First(); var backupName = Path.GetFileName(backup);
            await Docker(["run", "--rm", "--mount", $"type=bind,source={backups},target=/backups,readonly", (Environment.GetEnvironmentVariable("INVENTORY_TEST_IMAGE") ?? "remoteassist-inventory:1.1"), "verify-backup", "/backups/" + backupName]);
            await Docker(["run", "--rm", "--mount", $"type=bind,source={backups},target=/backups,readonly", "--mount", $"type=bind,source={restored},target=/restore", (Environment.GetEnvironmentVariable("INVENTORY_TEST_IMAGE") ?? "remoteassist-inventory:1.1"), "restore", "/backups/" + backupName, "/restore"]);
            Assert(File.Exists(Path.Combine(restored, "inventory.db")) && File.Exists(Path.Combine(restored, "photos", snapshot.Photos[0].Id)) && File.Exists(Path.Combine(restored, "config", "server.pfx")), "container CLI verifies and restores database, photo and TLS config");
            Console.WriteLine("7 Linux HTTPS container smoke checks passed. Scratch: " + scratch);
        }
        catch { if (running) Console.WriteLine(await Docker(["logs", name])); throw; }
        finally { if (running) await Docker(["stop", "-t", "15", name]); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static async Task<string> Docker(string[] args)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("Docker command exceeded 60 seconds."); }
        var text = await output; var err = await error; if (process.ExitCode != 0) throw new Exception($"Docker {args[0]} failed ({process.ExitCode}): {err}"); return text;
    }
}
