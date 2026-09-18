using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using RemoteAssist.Inventory;
using RemoteAssist.Inventory.Server;

if (args.Length > 0 && args[0] is "restore" or "verify-backup")
{
    if (args[0] == "verify-backup" && args.Length == 2) { InventoryStore.VerifyBackup(args[1]); Console.WriteLine("Backup integrity verified."); return; }
    if (args[0] == "restore" && args.Length == 3) { InventoryStore.RestoreBackup(args[1], args[2]); Console.WriteLine("Backup restored to an empty destination. Sessions invalidated. Configure the service to use this directory."); return; }
    throw new ArgumentException("Usage: verify-backup <backup-directory> | restore <backup-directory> <new-empty-data-directory>");
}

var builder = WebApplication.CreateBuilder(args);
var tlsPasswordFile = builder.Configuration["INVENTORY_TLS_PASSWORD_FILE"];
if (!string.IsNullOrWhiteSpace(tlsPasswordFile)) builder.Configuration["Kestrel:Certificates:Default:Password"] = File.ReadAllText(tlsPasswordFile).TrimEnd('\r', '\n');
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 21 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 25 * 1024 * 1024);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
var dataDirectory = builder.Configuration["INVENTORY_DATA_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);
using var serviceLock = new FileStream(Path.Combine(Path.GetFullPath(dataDirectory), "inventory.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var store = new InventoryStore(dataDirectory);
if (!store.HasUsers())
{
    var passwordFile = builder.Configuration["INVENTORY_OWNER_PASSWORD_FILE"];
    var owner = builder.Configuration["INVENTORY_OWNER_USERNAME"];
    if (string.IsNullOrWhiteSpace(passwordFile) || string.IsNullOrWhiteSpace(owner)) throw new InvalidOperationException("Initial owner required: set INVENTORY_OWNER_USERNAME and INVENTORY_OWNER_PASSWORD_FILE to a mounted secret file (12+ characters). No public bootstrap endpoint exists.");
    store.BootstrapOwner(owner, File.ReadAllText(passwordFile).TrimEnd('\r', '\n'));
}
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(s => s.GetRequiredService<BackupService>());
var app = builder.Build();
var allowHttp = string.Equals(builder.Configuration["INVENTORY_ALLOW_HTTP"], "true", StringComparison.OrdinalIgnoreCase);
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    try
    {
        if (!allowHttp && !ctx.Request.IsHttps && ctx.Request.Path != "/api/health") throw new StoreException(400, "https_required", "Подключение к общей базе требует HTTPS.");
        if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Path != "/api/login" && ctx.Request.Path != "/api/health")
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) throw new StoreException(401, "unauthorized", "Требуется вход в общую базу.");
            ctx.Items["inventoryUser"] = store.Authenticate(auth[7..]);
        }
        await next();
    }
    catch (StoreException ex) { ctx.Response.StatusCode = ex.Status; await ctx.Response.WriteAsJsonAsync(ex.Error); }
    catch (BadHttpRequestException ex) { ctx.Response.StatusCode = ex.StatusCode; await ctx.Response.WriteAsJsonAsync(new ApiError { Code = "bad_request", Message = "Некорректный запрос или слишком большой файл." }); }
    catch (Exception ex) { app.Logger.LogError(ex, "Inventory request failed: {Path}", ctx.Request.Path); ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new ApiError { Code = "internal", Message = "Ошибка сервера. Проверьте журнал службы; изменения не подтверждены." }); }
});
app.UseRateLimiter();
UserDto Actor(HttpContext ctx) => (UserDto)ctx.Items["inventoryUser"]!;
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "RemoteAssist.Inventory", version = 1 }));
app.MapPost("/api/login", (LoginRequest c) => store.Login(c)).RequireRateLimiting("login");
app.MapPost("/api/logout", (HttpContext ctx) => { store.Logout(ctx.Request.Headers.Authorization.ToString()[7..]); return Results.NoContent(); });
app.MapGet("/api/me", (HttpContext ctx) => Actor(ctx));
app.MapGet("/api/snapshot", () => store.Snapshot());
app.MapGet("/api/users", (HttpContext ctx) => store.Users(Actor(ctx)));
app.MapPost("/api/users", (SaveUserCommand c, HttpContext ctx) => store.SaveUser(c, Actor(ctx)));
app.MapPost("/api/rooms", (SaveCommand<RoomDto> c, HttpContext ctx) => store.SaveRoom(c, Actor(ctx)));
app.MapPost("/api/rooms/seed", (SeedRoomsCommand c, HttpContext ctx) => store.SeedRooms(c, Actor(ctx)));
app.MapPost("/api/assets", (SaveCommand<AssetDto> c, HttpContext ctx) => store.SaveAsset(c, Actor(ctx)));
app.MapPost("/api/import", (ImportCommand c, HttpContext ctx) => store.Import(c, Actor(ctx)));
app.MapPost("/api/cartridges", (SaveCommand<CartridgeDto> c, HttpContext ctx) => store.SaveCartridge(c, Actor(ctx)));
app.MapPost("/api/cartridges/replace", (ReplaceCartridgeCommand c, HttpContext ctx) => store.ReplaceCartridge(c, Actor(ctx)));
app.MapPost("/api/audits", (SaveCommand<AuditDto> c, HttpContext ctx) => store.CreateAudit(c, Actor(ctx)));
app.MapPost("/api/audits/items", (AuditItemCommand c, HttpContext ctx) => store.SaveAuditItem(c, Actor(ctx)));
app.MapPost("/api/audits/add-asset", (AuditAddAssetCommand c, HttpContext ctx) => store.AddAuditAsset(c, Actor(ctx)));
app.MapPost("/api/audits/complete", (AuditCompleteCommand c, HttpContext ctx) => store.CompleteAudit(c, Actor(ctx)));
app.MapPost("/api/photos", (SaveCommand<PhotoDto> c, HttpContext ctx) => store.SavePhoto(c, Actor(ctx)));
app.MapGet("/api/photos/{id}/content", (string id) => { var photo = store.Photo(id); return Results.File(photo.Path, photo.ContentType, enableRangeProcessing: false); });
app.MapPost("/api/photos/upload", async (HttpContext ctx) =>
{
    if (!ctx.Request.HasFormContentType) throw new StoreException(400, "invalid", "Требуется multipart/form-data.");
    var form = await ctx.Request.ReadFormAsync(); var file = form.Files.GetFile("file") ?? throw new StoreException(400, "invalid", "Выберите фотографию.");
    if (file.Length is <= 0 or > 20 * 1024 * 1024) throw new StoreException(400, "invalid", "Фотография должна быть не более 20 МБ.");
    using var memory = new MemoryStream(); await file.CopyToAsync(memory, ctx.RequestAborted);
    return store.UploadPhoto(form["commandId"].ToString(), form["ownerType"].ToString(), form["ownerId"].ToString(), form["caption"].ToString(), memory.ToArray(), Actor(ctx));
});
app.MapGet("/api/backups/status", (BackupService backups, HttpContext ctx) => { InventoryStore.Owner(Actor(ctx)); return backups.Status; });
app.MapPost("/api/backups", async (BackupService backups, HttpContext ctx) => { InventoryStore.Owner(Actor(ctx)); await backups.Run(ctx.RequestAborted); return Results.Ok(backups.Status); });
app.Run();

public partial class Program;
