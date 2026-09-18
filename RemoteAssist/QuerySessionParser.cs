namespace RemoteAssist;

public static class QuerySessionParser
{
    public static IReadOnlyList<UserSession> Parse(string output, string computer)
    {
        var items = new List<UserSession>();
        foreach (var raw in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var line = raw.TrimStart('>', ' '); var matches = Regex.Matches(line, "\\S+"); if (matches.Count < 3) continue;
            var tokens = matches.Select(m => m.Value).ToArray(); var idIndex = Array.FindIndex(tokens, t => int.TryParse(t, out _)); if (idIndex < 0 || idIndex + 1 >= tokens.Length) continue;
            var state = tokens[idIndex + 1]; if (!IsState(state)) continue;
            var user = idIndex >= 2 ? tokens[idIndex - 1] : null; var id = int.Parse(tokens[idIndex]);
            if (string.IsNullOrEmpty(user) || user.Equals("console", StringComparison.OrdinalIgnoreCase) || user.StartsWith("rdp-tcp", StringComparison.OrdinalIgnoreCase)) user = null;
            items.Add(new UserSession(computer, null, user, id, NormalizeState(state), DateTime.Now, null));
        }
        return items;
    }
    private static bool IsState(string state) => state.Equals("active", StringComparison.OrdinalIgnoreCase) || state.Equals("активно", StringComparison.OrdinalIgnoreCase) || state.Equals("disc", StringComparison.OrdinalIgnoreCase) || state.Equals("откл", StringComparison.OrdinalIgnoreCase) || state.Equals("idle", StringComparison.OrdinalIgnoreCase);
    private static string NormalizeState(string state) => state.Equals("active", StringComparison.OrdinalIgnoreCase) || state.Equals("активно", StringComparison.OrdinalIgnoreCase) ? "Active" : state;
}
