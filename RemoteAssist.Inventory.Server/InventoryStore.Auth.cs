using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RemoteAssist.Inventory;

namespace RemoteAssist.Inventory.Server;

public sealed partial class InventoryStore
{
    private const int PasswordIterations = 210_000;
    private static (string Salt, string Hash) PasswordHash(string password)
    {
        Require(password.Length >= 12, "Пароль должен содержать не менее 12 символов.");
        var salt = RandomNumberGenerator.GetBytes(32);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA512, 64)));
    }
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public void BootstrapOwner(string username, string password)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM users";
            if ((long)cmd.ExecuteScalar()! > 0) return;
            Require(!string.IsNullOrWhiteSpace(username), "Укажите логин владельца.");
            var user = new UserDto { Username = username.Trim(), DisplayName = username.Trim(), Role = "owner", Version = 1 };
            var hash = PasswordHash(password); cmd.CommandText = "INSERT INTO users(id,username,json,salt,hash) VALUES($id,$username,$json,$salt,$hash)";
            cmd.Parameters.AddWithValue("$id", user.Id); cmd.Parameters.AddWithValue("$username", user.Username); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(user, Json)); cmd.Parameters.AddWithValue("$salt", hash.Salt); cmd.Parameters.AddWithValue("$hash", hash.Hash); cmd.ExecuteNonQuery();
        }
    }
    public bool HasUsers() { lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM users"; return (long)cmd.ExecuteScalar()! > 0; } }
    public LoginResponse Login(LoginRequest request)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json,salt,hash FROM users WHERE username=$username"; cmd.Parameters.AddWithValue("$username", request.Username.Trim());
            UserDto? user = null; byte[] salt = new byte[32], expected = new byte[64];
            using (var reader = cmd.ExecuteReader()) if (reader.Read()) { user = JsonSerializer.Deserialize<UserDto>(reader.GetString(0), Json); salt = Convert.FromBase64String(reader.GetString(1)); expected = Convert.FromBase64String(reader.GetString(2)); }
            var actual = Rfc2898DeriveBytes.Pbkdf2(request.Password, salt, PasswordIterations, HashAlgorithmName.SHA512, 64);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected) || user is null || user.Disabled) throw new StoreException(401, "login_failed", "Неверный логин или пароль.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(48)); var expires = DateTimeOffset.UtcNow.AddHours(12);
            cmd.Parameters.Clear(); cmd.CommandText = "DELETE FROM sessions WHERE expires < $now; INSERT INTO sessions(hash,user_id,expires) VALUES($hash,$user,$expires)";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$hash", TokenHash(token)); cmd.Parameters.AddWithValue("$user", user.Id); cmd.Parameters.AddWithValue("$expires", expires.ToString("O")); cmd.ExecuteNonQuery();
            return new LoginResponse { Token = token, ExpiresUtc = expires, User = user };
        }
    }
    public UserDto Authenticate(string token)
    {
        if (token.Length != 96) throw new StoreException(401, "unauthorized", "Войдите в общую базу.");
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT u.json FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.hash=$hash AND s.expires > $now";
            cmd.Parameters.AddWithValue("$hash", TokenHash(token)); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            var json = cmd.ExecuteScalar() as string; var user = json is null ? null : JsonSerializer.Deserialize<UserDto>(json, Json);
            if (user is null || user.Disabled) throw new StoreException(401, "unauthorized", "Сеанс истёк. Войдите в общую базу."); return user;
        }
    }
    public void Logout(string token) { lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM sessions WHERE hash=$hash"; cmd.Parameters.AddWithValue("$hash", TokenHash(token)); cmd.ExecuteNonQuery(); } }
    public static void Owner(UserDto actor) { if (actor.Role != "owner") throw new StoreException(403, "forbidden", "Действие доступно владельцу базы."); }
    public List<UserDto> Users(UserDto actor)
    {
        Owner(actor); lock (gate) { using var db = Open(); return ReadUsers(db); }
    }
    private static List<UserDto> ReadUsers(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT json FROM users ORDER BY username"; using var reader = cmd.ExecuteReader(); List<UserDto> users = []; while (reader.Read()) users.Add(JsonSerializer.Deserialize<UserDto>(reader.GetString(0), Json)!); return users;
    }
    public MutationResult SaveUser(SaveUserCommand c, UserDto actor)
    {
        Owner(actor); RequireGuid(c.CommandId); RequireGuid(c.Value.Id);
        var fingerprint = TokenHash(JsonSerializer.Serialize(c, Json));
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(); using var prior = db.CreateCommand(); prior.Transaction = tx; prior.CommandText = "SELECT actor,fingerprint,result FROM commands WHERE id=$id"; prior.Parameters.AddWithValue("$id", c.CommandId);
            using (var reader = prior.ExecuteReader()) if (reader.Read()) { if (reader.GetString(0) != actor.Id || reader.GetString(1) != fingerprint) throw new StoreException(409, "command_reused", "Идентификатор операции использован."); var result = JsonSerializer.Deserialize<MutationResult>(reader.GetString(2), Json)!; result.AlreadyApplied = true; return result; }
            var users = ReadUsers(db, tx); var old = users.Find(x => x.Id == c.Value.Id); Version(old, c.Value.Version); var user = Copy(c.Value); user.Username = user.Username.Trim();
            Require(user.Username.Length is > 0 and <= 100 && user.DisplayName.Length <= 200, "Проверьте логин и имя пользователя."); Require(user.Role is "owner" or "editor", "Роль: owner или editor.");
            Require(!users.Any(x => x.Id != user.Id && string.Equals(x.Username, user.Username, StringComparison.OrdinalIgnoreCase)), "Логин занят.");
            if (old?.Role == "owner" && !old.Disabled && (user.Disabled || user.Role != "owner")) Require(users.Any(x => x.Id != user.Id && x.Role == "owner" && !x.Disabled), "Нельзя отключить последнего владельца базы.");
            Require(old is not null || c.Password is not null, "Для нового пользователя укажите пароль.");
            var password = c.Password is null ? ((string Salt, string Hash)?)null : PasswordHash(c.Password); user.Version++;
            using var save = db.CreateCommand(); save.Transaction = tx;
            save.CommandText = old is null ? "INSERT INTO users(id,username,json,salt,hash) VALUES($id,$username,$json,$salt,$hash)" : password is null ? "UPDATE users SET username=$username,json=$json WHERE id=$id" : "UPDATE users SET username=$username,json=$json,salt=$salt,hash=$hash WHERE id=$id";
            save.Parameters.AddWithValue("$id", user.Id); save.Parameters.AddWithValue("$username", user.Username); save.Parameters.AddWithValue("$json", JsonSerializer.Serialize(user, Json));
            if (password.HasValue) { save.Parameters.AddWithValue("$salt", password.Value.Salt); save.Parameters.AddWithValue("$hash", password.Value.Hash); } save.ExecuteNonQuery();
            if (password.HasValue || user.Disabled || old?.Role != user.Role) { save.CommandText = "DELETE FROM sessions WHERE user_id=$id"; save.ExecuteNonQuery(); }
            var state = Read(db, tx); state.Revision++; History(state, actor, "user", user.Id, old is null ? "Создан пользователь" : "Изменён пользователь", old, user); var response = new MutationResult { Revision = state.Revision };
            save.Parameters.Clear(); save.CommandText = "UPDATE state SET json=$json WHERE id=1; INSERT INTO commands(id,actor,fingerprint,result) VALUES($id,$actor,$fingerprint,$result)";
            save.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, Json)); save.Parameters.AddWithValue("$id", c.CommandId); save.Parameters.AddWithValue("$actor", actor.Id); save.Parameters.AddWithValue("$fingerprint", fingerprint); save.Parameters.AddWithValue("$result", JsonSerializer.Serialize(response, Json)); save.ExecuteNonQuery(); tx.Commit(); return response;
        }
    }
}
