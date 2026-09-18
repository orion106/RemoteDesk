using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using RemoteAssist;
using RemoteAssist.Inventory;

namespace RemoteAssist.Tests;

internal static class InventoryClientTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public static async Task Run(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "inventory-client"); Directory.CreateDirectory(root);
        var store = new LabStore(Path.Combine(root, "client.db"));
        using var credentials = new MemoryCredentials();
        await using var server = new LoopbackServer();
        using var client = new InventoryClient(store, credentials, Path.Combine(root, "photos"));
        check(Rejects(() => client.Configure("http://example.test", "admin")), "Inventory client rejects non-loopback cleartext endpoints.");
        check(Rejects(() => client.Configure("https://admin:secret@example.test", "admin")), "Inventory client rejects credentials embedded in its endpoint URL.");
        check(Rejects(() => client.Configure("https://example.test/?token=fixture", "admin")), "Inventory client rejects endpoint query strings that could contain credentials.");
        await client.LoginAsync(server.Url, "admin", "fixture-password").WaitAsync(Timeout);
        check(client.Online && client.Authenticated && client.Snapshot.InstanceId == server.InstanceId,
            "Real loopback login authenticates and loads the shared inventory snapshot.");
        check(server.LastLogin is { Username: "admin", Password: "fixture-password" } && credentials.Count == 1,
            "Login sends the entered credentials and stores only the returned bearer token in the credential store.");
        check(store.Load<InventoryConnection>("inventory-settings").Single().Username == "admin" &&
            store.Load<InventoryClient.InventoryCache>("inventory-cache").Single().Snapshot.Revision == 1,
            "Connection metadata and the last confirmed inventory snapshot persist locally.");

        using (var restarted = new InventoryClient(store, credentials, Path.Combine(root, "photos")))
        {
            check(!restarted.Online && restarted.Authenticated && restarted.Snapshot.InstanceId == server.InstanceId,
                "Restart restores the offline snapshot and saved token without claiming a live connection.");
            await restarted.RefreshAsync().WaitAsync(Timeout);
            check(restarted.Online && server.AuthorizedSnapshotRequests >= 2, "A restored client reconnects using the saved bearer token.");
            server.FailSnapshots = true;
            var unavailable = await Capture(() => restarted.RefreshAsync());
            check(unavailable is InventoryApiException { Status: HttpStatusCode.ServiceUnavailable } && !restarted.Online && restarted.Snapshot.Revision == 1,
                "A failed refresh switches to offline state without discarding the last valid snapshot.");
            var writesBefore = server.WriteRequests.Count;
            check(await Capture(() => restarted.SaveAsync("assets", new AssetDto())) is InvalidOperationException && server.WriteRequests.Count == writesBefore,
                "Offline writes are rejected before any mutation request reaches the server.");
            server.FailSnapshots = false; await restarted.RefreshAsync().WaitAsync(Timeout);
            check(restarted.Online, "Refreshing after a transient outage restores online state.");
            server.RejectSnapshots = true;
            var unauthorized = await Capture(() => restarted.RefreshAsync());
            check(unauthorized is InventoryApiException { Status: HttpStatusCode.Unauthorized } && !restarted.Authenticated && !restarted.Online,
                "An HTML 401 response clears authentication and retains the HTTP error status.");
            check(restarted.Snapshot.InstanceId == server.InstanceId, "Authentication failure retains the read-only cached inventory.");
            server.RejectSnapshots = false;
        }

        server.TruncateNextWriteResponse = true;
        var asset = new AssetDto { Model = "Fixture system", InventoryNumber = "000123" };
        await client.SaveAsync("assets", asset).WaitAsync(Timeout);
        var attempts = server.WriteRequests.ToArray();
        check(attempts.Length == 2 && attempts[0].Body == attempts[1].Body,
            "A truncated mutation response retries the identical serialized command rather than creating a new request identity.");
        var ids = attempts.Select(x => JsonSerializer.Deserialize<SaveCommand<AssetDto>>(x.Body, Json)!.CommandId).Distinct().ToArray();
        check(ids.Length == 1 && server.AppliedWrites == 1 && client.Snapshot.Assets.Single().InventoryNumber == "000123",
            "Transport retry uses one idempotency key and the server applies the mutation exactly once.");

        server.FailSnapshotsAfterNextWrite = true;
        var next = new AssetDto { Model = "Second fixture", InventoryNumber = "000124" };
        var committed = await Capture(() => client.SaveAsync("assets", next));
        check(committed is InventoryWriteCommittedException && server.AppliedWrites == 2 && !client.Online,
            "A successful write followed by failed refresh reports a confirmed commit, not an ambiguous save failure.");
        check(client.Snapshot.Assets.Count == 1, "A failed post-write refresh keeps the last complete snapshot until reconciliation.");
        server.FailSnapshots = false; await client.RefreshAsync().WaitAsync(Timeout);
        check(client.Snapshot.Assets.Count == 2 && server.AppliedWrites == 2,
            "Reconnecting discovers the committed object without resubmitting the mutation.");

        var photoPath = await client.PhotoPathAsync(new PhotoDto { Id = "small-photo" }).WaitAsync(Timeout);
        check(photoPath is not null && File.ReadAllBytes(photoPath).SequenceEqual(LoopbackServer.PhotoBytes),
            "Photo download writes a reusable local cache entry.");
        var photoRequests = server.PhotoRequests;
        check(await client.PhotoPathAsync(new PhotoDto { Id = "small-photo" }).WaitAsync(Timeout) == photoPath && server.PhotoRequests == photoRequests,
            "Repeated photo preview uses the cached file without a second network request.");
        check(await Capture(async () => { await client.PhotoPathAsync(new PhotoDto { Id = "../escape" }); }) is InvalidDataException,
            "Server-provided photo IDs cannot escape the photo cache directory.");
        check(await Capture(async () => { await client.PhotoPathAsync(new PhotoDto { Id = "oversized-photo" }); }) is InvalidDataException,
            "An oversized photo response is rejected from its headers before buffering the body.");

        await client.LogoutAsync().WaitAsync(Timeout);
        check(server.LogoutRequests == 1 && !client.Authenticated && !client.Online && credentials.Count == 0,
            "Logout revokes the server session and removes the saved local token.");
        check(client.Snapshot.Assets.Count == 2 && await client.PhotoPathAsync(new PhotoDto { Id = "small-photo" }).WaitAsync(Timeout) == photoPath,
            "Logout keeps the explicit offline snapshot and already downloaded photo cache available.");
        server.ThrowIfFaulted();
    }

    private static bool Rejects(Action action)
    {
        try { action(); return false; } catch (InvalidOperationException) { return true; }
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action().WaitAsync(Timeout); return null; } catch (TimeoutException) { throw; } catch (Exception error) { return error; }
    }

    private sealed class MemoryCredentials : ICredentialStore, IDisposable
    {
        private readonly Dictionary<string, SecureString> _values = [];
        public int Count => _values.Count;
        public SecureString? Read(string key) => _values.TryGetValue(key, out var value) ? value.Copy() : null;
        public void Write(string key, string username, SecureString value) { Delete(key); _values[key] = value.Copy(); }
        public void Delete(string key) { if (_values.Remove(key, out var value)) value.Dispose(); }
        public void Dispose() { foreach (var value in _values.Values) value.Dispose(); _values.Clear(); }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body);
    private sealed record Response(int Status, byte[] Body, string ContentType = "application/json", long? DeclaredLength = null);

    /// <summary>A small real HTTP/1.1 server; no HTTP.sys registration or administrator rights required.</summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        public static readonly byte[] PhotoBytes = [137, 80, 78, 71, 13, 10, 26, 10];
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly ConcurrentQueue<Exception> _errors = new();
        private readonly HashSet<string> _appliedCommands = [];
        private readonly object _sync = new();
        private readonly Task _accept;
        private readonly InventorySnapshot _snapshot;
        public string InstanceId { get; } = Guid.NewGuid().ToString("N");
        public string Url { get; }
        public LoginRequest? LastLogin { get; private set; }
        public ConcurrentQueue<Request> WriteRequests { get; } = new();
        public volatile bool FailSnapshots, RejectSnapshots, TruncateNextWriteResponse, FailSnapshotsAfterNextWrite;
        public int AppliedWrites, AuthorizedSnapshotRequests, LogoutRequests, PhotoRequests;

        public LoopbackServer()
        {
            _snapshot = new() { InstanceId = InstanceId, Revision = 1, RetrievedUtc = DateTimeOffset.UtcNow };
            _listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    _connections.Add(ServeAsync(client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var stream = client.GetStream(); var requestLine = await ReadLine(stream, timeout.Token).ConfigureAwait(false);
                    var parts = requestLine.Split(' ');
                    if (parts.Length < 3) throw new InvalidDataException("Malformed loopback request.");
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var count = 0; ; count++)
                    {
                        if (count > 100) throw new InvalidDataException("Too many loopback headers.");
                        var line = await ReadLine(stream, timeout.Token).ConfigureAwait(false); if (line.Length == 0) break;
                        var separator = line.IndexOf(':'); if (separator <= 0) throw new InvalidDataException("Malformed loopback header.");
                        headers[line[..separator]] = line[(separator + 1)..].Trim();
                    }
                    if (headers.GetValueOrDefault("Expect")?.Contains("100-continue", StringComparison.OrdinalIgnoreCase) == true)
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), timeout.Token).ConfigureAwait(false);
                    var body = await ReadBody(stream, headers, timeout.Token).ConfigureAwait(false);
                    var request = new Request(parts[0], new Uri(new Uri(Url), parts[1]).AbsolutePath, headers, Encoding.UTF8.GetString(body));
                    Response response; lock (_sync) response = Handle(request);
                    var reason = response.Status switch { 200 => "OK", 204 => "No Content", 401 => "Unauthorized", 503 => "Service Unavailable", _ => "Not Found" };
                    var wireHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} {reason}\r\nContent-Type: {response.ContentType}\r\nContent-Length: {response.DeclaredLength ?? response.Body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(wireHeaders, timeout.Token).ConfigureAwait(false);
                    if (response.Body.Length > 0) await stream.WriteAsync(response.Body, timeout.Token).ConfigureAwait(false);
                    await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) { /* A bounded download may reject the headers and close its connection early. */ }
                catch (Exception error) { _errors.Enqueue(error); }
            }
        }

        private Response Handle(Request request)
        {
            if (request.Path == "/api/login" && request.Method == "POST")
            {
                LastLogin = JsonSerializer.Deserialize<LoginRequest>(request.Body, Json);
                return JsonResponse(new LoginResponse { Token = "fixture-bearer-token", User = new() { Username = "admin", Role = "owner" }, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) });
            }
            if (request.Headers.GetValueOrDefault("Authorization") != "Bearer fixture-bearer-token")
                return new(401, Encoding.UTF8.GetBytes("<html>Unauthorized</html>"), "text/html");
            if (request.Path == "/api/logout") { LogoutRequests++; return new(204, []); }
            if (request.Path == "/api/snapshot")
            {
                AuthorizedSnapshotRequests++;
                if (RejectSnapshots) return new(401, Encoding.UTF8.GetBytes("<html>Expired session</html>"), "text/html");
                if (FailSnapshots) return JsonResponse(new ApiError { Code = "unavailable", Message = "Fixture outage" }, 503);
                _snapshot.RetrievedUtc = DateTimeOffset.UtcNow; return JsonResponse(_snapshot);
            }
            if (request.Path == "/api/assets" && request.Method == "POST")
            {
                WriteRequests.Enqueue(request);
                var command = JsonSerializer.Deserialize<SaveCommand<AssetDto>>(request.Body, Json)!;
                var alreadyApplied = !_appliedCommands.Add(command.CommandId);
                if (!alreadyApplied) { AppliedWrites++; command.Value.Version = 1; _snapshot.Assets.Add(command.Value); _snapshot.Revision++; }
                if (FailSnapshotsAfterNextWrite) { FailSnapshotsAfterNextWrite = false; FailSnapshots = true; }
                if (TruncateNextWriteResponse) { TruncateNextWriteResponse = false; return new(200, Encoding.UTF8.GetBytes("{"), DeclaredLength: 50); }
                return JsonResponse(new MutationResult { Revision = _snapshot.Revision, AlreadyApplied = alreadyApplied });
            }
            if (request.Path == "/api/photos/small-photo/content") { PhotoRequests++; return new(200, PhotoBytes, "image/png"); }
            if (request.Path == "/api/photos/oversized-photo/content") { PhotoRequests++; return new(200, [], "image/png", 20 * 1024 * 1024 + 1); }
            return JsonResponse(new ApiError { Message = "Fixture route not found: " + request.Path }, 404);
        }

        private static Response JsonResponse<T>(T value, int status = 200) => new(status, JsonSerializer.SerializeToUtf8Bytes(value, Json));

        private static async Task<string> ReadLine(Stream stream, CancellationToken token)
        {
            using var line = new MemoryStream(); var one = new byte[1];
            while (line.Length <= 8192)
            {
                var read = await stream.ReadAsync(one, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                if (one[0] == '\n') return Encoding.ASCII.GetString(line.ToArray()).TrimEnd('\r');
                line.WriteByte(one[0]);
            }
            throw new InvalidDataException("Loopback HTTP line exceeds test bound.");
        }

        private static async Task<byte[]> ReadBody(Stream stream, Dictionary<string, string> headers, CancellationToken token)
        {
            using var body = new MemoryStream();
            if (headers.GetValueOrDefault("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
            {
                while (true)
                {
                    var sizeLine = await ReadLine(stream, token).ConfigureAwait(false);
                    var size = Convert.ToInt32(sizeLine.Split(';')[0], 16);
                    if (size == 0) { while ((await ReadLine(stream, token).ConfigureAwait(false)).Length != 0) { } break; }
                    if (size < 0 || body.Length + size > 1024 * 1024) throw new InvalidDataException("Loopback body exceeds test bound.");
                    var chunk = new byte[size]; await stream.ReadExactlyAsync(chunk, token).ConfigureAwait(false); body.Write(chunk);
                    if ((await ReadLine(stream, token).ConfigureAwait(false)).Length != 0) throw new InvalidDataException("Malformed chunk terminator.");
                }
            }
            else if (headers.TryGetValue("Content-Length", out var lengthText))
            {
                var length = int.Parse(lengthText, System.Globalization.CultureInfo.InvariantCulture);
                if (length is < 0 or > 1024 * 1024) throw new InvalidDataException("Loopback body exceeds test bound.");
                var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false); body.Write(bytes);
            }
            return body.ToArray();
        }

        public void ThrowIfFaulted()
        {
            if (_errors.TryPeek(out var error)) throw new InvalidOperationException("Loopback inventory test server failed.", error);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            await _accept.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Task.WhenAll(_connections.ToArray()).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
