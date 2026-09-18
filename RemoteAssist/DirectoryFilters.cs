using System.Net;
using System.Security;

namespace RemoteAssist;

public static class DirectoryFilters
{
    public static string Escape(string value) => value.Replace("\\", "\\5c").Replace("*", "\\2a")
        .Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

    public static string[] ParseMasks(string text)
    {
        var masks = text.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (masks.Length == 0) throw new InvalidOperationException("Добавьте хотя бы одну маску компьютеров.");
        if (masks.Any(m => m.Any(char.IsControl) || m.Any(char.IsWhiteSpace) || m.Contains('?') || m.Contains('/')))
            throw new InvalidOperationException("Маска не должна содержать пробелы, / или ?. Используйте * для любого числа символов.");
        return masks;
    }

    public static string Computers(string query, IReadOnlyList<string> masks)
    {
        if (masks.Count == 0) throw new InvalidOperationException("Область поиска не задана: добавьте маски компьютеров.");
        // Only * in configured masks is a wildcard. User-entered search text is always literal.
        var maskFilter = "(|" + string.Concat(masks.Select(m => "(name=" + string.Join("*", m.Split('*').Select(Escape)) + ")")) + ")";
        var searchFilter = string.IsNullOrWhiteSpace(query) ? "" : $"(|(name=*{Escape(query)}*)(dNSHostName=*{Escape(query)}*))";
        return $"(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2)){maskFilter}{searchFilter})";
    }

    public static string Users(string query) => $"(&(objectCategory=person)(objectClass=user)(|(displayName=*{Escape(query)}*)(sAMAccountName=*{Escape(query)}*)(userPrincipalName=*{Escape(query)}*)))";

    public static bool AllowsComputer(string name, IEnumerable<string> masks) => masks.Any(mask =>
        Regex.IsMatch(name, "\\A" + Regex.Escape(mask).Replace("\\*", ".*") + "\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    public static NetworkCredential Credentials(string login, SecureString password)
    {
        login = login.Trim();
        var slash = login.IndexOf('\\');
        if (slash > 0 && slash < login.Length - 1 && login.LastIndexOf('\\') == slash)
            return new NetworkCredential(login[(slash + 1)..], password, login[..slash]);
        if (!login.Contains('\\') && login.IndexOf('@') > 0 && !login.EndsWith('@'))
            return new NetworkCredential(login, password);
        throw new InvalidOperationException("Введите логин в формате DOMAIN\\user или user@domain.");
    }
}
