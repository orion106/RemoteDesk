using System.Net;
using System.Net.Http;

namespace RemoteAssist;

public interface IInventorySource
{
    Task<IReadOnlyList<LabComputer>> ReadAsync(CancellationToken token);
}

public sealed class GlpiInventorySource(LabOptions options, LabSecrets secrets, HttpMessageHandler? handler = null) : IInventorySource
{
    public async Task<IReadOnlyList<LabComputer>> ReadAsync(CancellationToken token)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(options.GlpiUrl)) throw new InvalidOperationException("Укажите адрес GLPI в настройках классов.");
        var url = options.GlpiUrl.TrimEnd('/');
        if (!url.EndsWith("apirest.php", StringComparison.OrdinalIgnoreCase)) url += "/apirest.php";
        using var client = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler, false);
        client.BaseAddress = new Uri(url + "/"); client.Timeout = TimeSpan.FromSeconds(30);
        var userToken = secrets.Read("glpi-user", options.GlpiUrl);
        if (userToken.Length == 0) throw new InvalidOperationException("Сохраните пользовательский API-токен GLPI.");
        var appToken = secrets.Read("glpi-app", options.GlpiUrl);
        if (appToken.Length > 0) client.DefaultRequestHeaders.Add("App-Token", appToken);
        using var init = new HttpRequestMessage(HttpMethod.Get, "initSession"); init.Headers.TryAddWithoutValidation("Authorization", "user_token " + userToken);
        using var initReply = await client.SendAsync(init, token); await Ensure(initReply, token);
        var sessionJson = await Parse(initReply, token);
        client.DefaultRequestHeaders.Add("Session-Token", sessionJson.GetProperty("session_token").GetString());
        try
        {
            var rows = await List(client, "Computer", token);
            if (options.GlpiEntity.Length > 0) rows = rows.Where(x => Field(x, "entities_id") == options.GlpiEntity).ToList();
            var computers = new ConcurrentBag<LabComputer>(); using var gate = new SemaphoreSlim(4);
            var locations = new ConcurrentDictionary<string, string>();
            await Task.WhenAll(rows.Where(x => Field(x, "is_deleted") != "1" && Field(x, "is_template") != "1").Select(async row =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var id = Field(row, "id"); if (!int.TryParse(id, out _)) return;
                    var pc = new LabComputer { Name = Field(row, "name"), Uuid = Field(row, "uuid"), Serial = Field(row, "serial"), GlpiIds = [new Uri(url).GetLeftPart(UriPartial.Path) + "#" + id] };
                    var locationId = Field(row, "locations_id");
                    if (int.TryParse(locationId, out var location) && location > 0)
                    {
                        if (!locations.TryGetValue(locationId, out var name)) { var data = await Get(client, "Location/" + locationId, token); name = Field(data, "completename"); if (name.Length == 0) name = Field(data, "name"); locations[locationId] = name; }
                        pc.Room = name;
                    }
                    pc.Manufacturer = await Dropdown(client, "Manufacturer", Field(row, "manufacturers_id"), token);
                    pc.Model = await Dropdown(client, "ComputerModel", Field(row, "computermodels_id"), token);
                    var addresses = new List<string>();
                    try
                    {
                        foreach (var port in await List(client, $"Computer/{id}/NetworkPort", token))
                        {
                            var mac = Field(port, "mac"); if (mac.Length > 0) pc.MacAddresses.Add(mac);
                            var portId = Field(port, "id");
                            foreach (var network in await List(client, $"NetworkPort/{portId}/NetworkName", token))
                                foreach (var ip in await List(client, $"NetworkName/{Field(network, "id")}/IPAddress", token))
                                    if (IPAddress.TryParse(Field(ip, "name"), out var address) && !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal) addresses.Add(address.ToString());
                        }
                    }
                    catch (HttpRequestException) { pc.InventoryNote = "GLPI не предоставил часть сетевых сведений; проверьте адреса."; }
                    var endpoints = new List<(LabOs Os, string Version)>();
                    try
                    {
                        foreach (var os in await List(client, $"Computer/{id}/Item_OperatingSystem", token))
                        {
                            var name = await Dropdown(client, "OperatingSystem", Field(os, "operatingsystems_id"), token);
                            var version = await Dropdown(client, "OperatingSystemVersion", Field(os, "operatingsystemversions_id"), token);
                            endpoints.Add((name.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? LabOs.Windows : name.Contains("Astra", StringComparison.OrdinalIgnoreCase) || name.Contains("Астра", StringComparison.OrdinalIgnoreCase) ? LabOs.Astra : LabOs.Unknown, name + " " + version));
                        }
                    }
                    catch (HttpRequestException) { pc.InventoryNote += " ОС не предоставлена API."; }
                    if (endpoints.Count == 0) endpoints.Add((LabOs.Unknown, "ОС не указана в GLPI"));
                    var checkedAt = DateTimeOffset.TryParse(Field(row, "last_inventory_update"), out var date) ? date : (DateTimeOffset?)null;
                    var hosts = addresses.Distinct().ToList(); if (hosts.Count == 0 && pc.Name.Length > 0) hosts.Add(pc.Name);
                    foreach (var endpoint in endpoints.Distinct()) foreach (var host in hosts) pc.Systems.Add(new() { Os = endpoint.Os, Address = host, Description = endpoint.Version, InventoryAt = checkedAt });
                    pc.MacAddresses = pc.MacAddresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    computers.Add(pc);
                }
                finally { gate.Release(); }
            }));
            return computers.OrderBy(x => x.Room).ThenBy(x => x.Name).ToArray();
        }
        finally
        {
            try { using var end = await client.GetAsync("killSession", CancellationToken.None); } catch (HttpRequestException) { } catch (TaskCanceledException) { }
        }
    }
    private static async Task<string> Dropdown(HttpClient client, string type, string id, CancellationToken token) => int.TryParse(id, out var numeric) && numeric > 0 ? Field(await Get(client, type + "/" + id, token), "name") : "";
    internal static string Field(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
    private static async Task<JsonElement> Get(HttpClient client, string path, CancellationToken token)
    {
        using var reply = await client.GetAsync(path, token); await Ensure(reply, token); return await Parse(reply, token);
    }
    private static async Task<List<JsonElement>> List(HttpClient client, string path, CancellationToken token)
    {
        var list = new List<JsonElement>();
        for (var start = 0; ; start += 200)
        {
            using var reply = await client.GetAsync(path + $"?range={start}-{start + 199}", token);
            if (reply.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) break;
            await Ensure(reply, token); var items = await Parse(reply, token);
            if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GLPI вернул неожиданный формат списка.");
            var batch = items.EnumerateArray().ToArray(); list.AddRange(batch);
            if (batch.Length < 200) break;
            if (reply.Content.Headers.ContentRange?.Length is long total && list.Count >= total) break;
        }
        return list;
    }
    private static async Task<JsonElement> Parse(HttpResponseMessage reply, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(token)); return document.RootElement.Clone();
    }
    private static Task Ensure(HttpResponseMessage reply, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!reply.IsSuccessStatusCode) throw new HttpRequestException($"GLPI API: HTTP {(int)reply.StatusCode}. Проверьте адрес API, токены, права и область доступа.", null, reply.StatusCode);
        return Task.CompletedTask;
    }
}
