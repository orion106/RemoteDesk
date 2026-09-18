using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security;
using System.Security.Cryptography;
using RemoteAssist.Inventory;

namespace RemoteAssist;

public sealed class InventoryConnection
{
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
}
public sealed class InventoryApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
public sealed class InventoryWriteCommittedException(Exception cause) : Exception("Изменение сохранено на сервере, но получить обновлённые данные не удалось. Обновите соединение перед следующими изменениями. " + cause.Message, cause);

public sealed class InventoryClient : IDisposable
{
    private readonly LabStore _store;
    private readonly ICredentialStore _credentials;
    private HttpClient _http;
    private string _cacheKey = "";
    public InventoryConnection Connection { get; private set; }
    public InventorySnapshot Snapshot { get; private set; } = new();
    public UserDto? User { get; private set; }
    public bool Online { get; private set; }
    public bool Authenticated => _http.DefaultRequestHeaders.Authorization is not null;
    public event Action? Changed;
    public string PhotoCacheDirectory { get; }
    public InventoryClient(LabStore store, ICredentialStore credentials, string? cacheDirectory = null)
    {
        _store = store; _credentials = credentials;
        _http = NewHttp();
        Connection = store.Load<InventoryConnection>("inventory-settings").FirstOrDefault() ?? new();
        PhotoCacheDirectory = cacheDirectory ?? Path.Combine(Path.GetDirectoryName(LabStore.DefaultPath)!, "inventory-photo-cache");
        if (Connection.Url.Length > 0) Configure(Connection.Url, Connection.Username);
    }
    private static HttpClient NewHttp() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public void Configure(string url, string username)
    {
        if (!Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidOperationException("Укажите HTTPS-адрес службы инвентаризации. HTTP разрешён только для локального тестирования.");
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new InvalidOperationException("Адрес службы не должен содержать пароль, параметры или фрагмент.");
        var next = NewHttp(); next.BaseAddress = uri; _http.Dispose(); _http = next;
        Connection = new() { Url = uri.ToString(), Username = username.Trim() };
        _cacheKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Connection.Url)));
        Snapshot = _store.Load<InventoryCache>("inventory-cache").FirstOrDefault(x => x.Key == _cacheKey)?.Snapshot ?? new();
        using var token = _credentials.Read(TokenKey);
        if (token is not null) _http.DefaultRequestHeaders.Authorization = new("Bearer", new NetworkCredential("", token).Password);
        Online = false; User = null; Changed?.Invoke();
    }
    private string TokenKey => $"RemoteAssist/Inventory/{_cacheKey}/{Connection.Username}";
    public async Task LoginAsync(string url, string username, string password)
    {
        Configure(url, username);
        using var response = await _http.PostAsJsonAsync("api/login", new LoginRequest { Username = username, Password = password });
        await Check(response); var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        _http.DefaultRequestHeaders.Authorization = new("Bearer", login.Token); User = login.User;
        using var token = new SecureString(); foreach (var c in login.Token) token.AppendChar(c); token.MakeReadOnly();
        _credentials.Write(TokenKey, username, token);
        _store.Save("inventory-settings", "connection", Connection);
        await RefreshAsync();
    }
    public async Task LogoutAsync()
    {
        try { if (Authenticated) { using var response = await _http.PostAsync("api/logout", null); } } catch (HttpRequestException) { } catch (TaskCanceledException) { }
        if (_cacheKey.Length > 0) _credentials.Delete(TokenKey);
        _http.DefaultRequestHeaders.Authorization = null; User = null; Online = false; Changed?.Invoke();
    }
    public async Task RefreshAsync()
    {
        if (_http.BaseAddress is null || !Authenticated) { Online = false; Changed?.Invoke(); return; }
        try
        {
            using var response = await _http.GetAsync("api/snapshot"); await Check(response);
            var snapshot = await response.Content.ReadFromJsonAsync<InventorySnapshot>() ?? throw new InvalidDataException("Пустой ответ сервера.");
            Snapshot = snapshot; Online = true;
            _store.Save("inventory-cache", _cacheKey, new InventoryCache { Key = _cacheKey, Snapshot = snapshot });
        }
        catch { Online = false; throw; }
        finally { Changed?.Invoke(); }
    }
    public async Task PostAsync<T>(string route, T body)
    {
        if (!Online) throw new InvalidOperationException("Для сохранения подключитесь к серверу.");
        // Same serialized command and id are reused only for uncertain transport failures.
        for (var attempt = 0; ; attempt++)
        {
            try { using var response = await _http.PostAsJsonAsync(route, body); await Check(response); break; }
            catch (Exception e) when (attempt == 0 && e is HttpRequestException or TaskCanceledException) { }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Online = false; Changed?.Invoke(); throw; }
        }
        try { await RefreshAsync(); } catch (Exception e) { throw new InventoryWriteCommittedException(e); }
    }
    public Task SaveAsync<T>(string collection, T value) => PostAsync("api/" + collection, new SaveCommand<T> { Value = value });
    public async Task<List<UserDto>> UsersAsync()
    {
        using var response = await _http.GetAsync("api/users"); await Check(response);
        return await response.Content.ReadFromJsonAsync<List<UserDto>>() ?? [];
    }
    public async Task UploadPhotoAsync(string path, string ownerType, string ownerId, string caption)
    {
        if (!Online) throw new InvalidOperationException("Для загрузки фотографии подключитесь к серверу.");
        var info = new FileInfo(path);
        if (info.Length > 20 * 1024 * 1024) throw new InvalidOperationException("Максимальный размер фотографии — 20 МБ.");
        if (Path.GetExtension(path).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png")) throw new InvalidOperationException("Выберите JPG или PNG.");
        var commandId = Guid.NewGuid().ToString("N");
        for (var attempt = 0; ; attempt++)
        {
            using var form = new MultipartFormDataContent(); using var file = File.OpenRead(path);
            var content = new StreamContent(file); content.Headers.ContentType = new(Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
            form.Add(content, "file", Path.GetFileName(path)); form.Add(new StringContent(ownerType), "ownerType"); form.Add(new StringContent(ownerId), "ownerId"); form.Add(new StringContent(caption), "caption"); form.Add(new StringContent(commandId), "commandId");
            try { using var response = await _http.PostAsync("api/photos/upload", form); await Check(response); break; }
            catch (Exception e) when (attempt == 0 && e is HttpRequestException or TaskCanceledException) { }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Online = false; Changed?.Invoke(); throw; }
        }
        try { await RefreshAsync(); } catch (Exception e) { throw new InventoryWriteCommittedException(e); }
    }
    public async Task<string?> PhotoPathAsync(PhotoDto photo)
    {
        // Server IDs are untrusted input, even on a trusted NAS.
        if (!Regex.IsMatch(photo.Id, "^[a-zA-Z0-9-]{1,80}$")) throw new InvalidDataException("Некорректный идентификатор фотографии.");
        var directory = Path.Combine(PhotoCacheDirectory, _cacheKey); var path = Path.Combine(directory, photo.Id + ".image");
        if (File.Exists(path)) return path;
        if (!Online) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await _http.GetAsync("api/photos/" + Uri.EscapeDataString(photo.Id) + "/content", HttpCompletionOption.ResponseHeadersRead, timeout.Token); await Check(response);
        const int limit = 20 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Фотография слишком велика.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token); using var buffer = new MemoryStream(); var chunk = new byte[65536]; int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0) { if (buffer.Length + read > limit) throw new InvalidDataException("Фотография слишком велика."); buffer.Write(chunk, 0, read); }
        var bytes = buffer.ToArray();
        Directory.CreateDirectory(directory); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes); File.Move(temp, path, true); return path;
    }
    private async Task Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized) { Online = false; _http.DefaultRequestHeaders.Authorization = null; }
        ApiError? error = null;
        try { error = await response.Content.ReadFromJsonAsync<ApiError>().ConfigureAwait(false); } catch (JsonException) { } catch (NotSupportedException) { }
        throw new InventoryApiException(response.StatusCode, error?.Message ?? $"Сервер вернул {(int)response.StatusCode}.");
    }
    public void Dispose() => _http.Dispose();
    public sealed class InventoryCache { public string Key { get; set; } = ""; public InventorySnapshot Snapshot { get; set; } = new(); }
}
