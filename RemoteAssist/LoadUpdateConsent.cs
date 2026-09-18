namespace RemoteAssist;

public enum UpdateConsent { Yes, No, Timeout, Unavailable }
public sealed record LoadProcess(uint Id, string Path, string Created, int SessionId, string Owner, string OwnerSid)
{
    public bool SameAs(LoadProcess other) => Id == other.Id && Created == other.Created && SessionId == other.SessionId &&
        Path.Equals(other.Path, StringComparison.OrdinalIgnoreCase) && Owner.Equals(other.Owner, StringComparison.OrdinalIgnoreCase) && OwnerSid == other.OwnerSid;
}
public interface ILoadProcesses
{
    IReadOnlyList<LoadProcess> Find(string computer, string path);
    bool ConfirmSession(string computer, LoadProcess process);
    UpdateConsent Ask(string computer, LoadProcess process);
    void RequestClose(string computer, LoadProcess process);
    void Terminate(string computer, LoadProcess process);
}
public interface ILoadConsentCoordinator { LoadUpdateResult? CloseWithConsent(LoadInstallation installation, CancellationToken token); }

public sealed class LoadConsentCoordinator(ILoadProcesses processes, Action<TimeSpan, CancellationToken>? wait = null) : ILoadConsentCoordinator
{
    public const string Prompt = "Доступно обновление программы “Нагрузка”. Сохраните работу. При нажатии “Да” приложение будет закрыто. Если оно не закроется за 15 секунд, оно будет завершено принудительно; несохранённые данные могут быть потеряны. Обновить сейчас?";
    public LoadUpdateResult? CloseWithConsent(LoadInstallation installation, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var approved = processes.Find(installation.Computer, installation.Path);
            if (approved.Any(p => !p.Path.Equals(installation.Path, StringComparison.OrdinalIgnoreCase) || p.SessionId <= 0 || string.IsNullOrWhiteSpace(p.OwnerSid) || string.IsNullOrWhiteSpace(p.Owner)))
                return Deferred("Не удалось подтвердить путь, владельца или пользовательский сеанс процесса.");
            foreach (var session in approved.GroupBy(p => p.SessionId))
            {
                token.ThrowIfCancellationRequested();
                var representative = session.First();
                if (session.Any(p => p.OwnerSid != representative.OwnerSid) || !processes.ConfirmSession(installation.Computer, representative))
                    return Deferred("Пользовательский сеанс недоступен или его владелец изменился.");
                var answer = processes.Ask(installation.Computer, representative);
                token.ThrowIfCancellationRequested(); // A late Yes is never actionable after cancellation.
                if (answer != UpdateConsent.Yes) return answer switch
                {
                    UpdateConsent.No => new(LoadUpdateState.Declined, "Пользователь выбрал «Нет». Приложение оставлено открытым."),
                    UpdateConsent.Timeout => new(LoadUpdateState.TimedOut, "За пять минут ответ не получен. Приложение оставлено открытым."),
                    _ => Deferred("Не удалось показать запрос или подтвердить ответ пользователя.")
                };
            }
            var current = processes.Find(installation.Computer, installation.Path);
            if (!Unchanged(approved, current)) return Deferred("Процессы изменились. Нужен новый запрос пользователям.");
            // All sessions must agree before any process is touched.
            if (current.Any(p => !processes.ConfirmSession(installation.Computer, p))) return Deferred("Сеанс изменился после ответа. Нужен новый запрос.");
            foreach (var process in current)
            {
                token.ThrowIfCancellationRequested();
                processes.RequestClose(installation.Computer, process);
            }
            if (current.Count > 0)
            {
                if (wait is not null) wait(TimeSpan.FromSeconds(15), token);
                else if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(15))) token.ThrowIfCancellationRequested();
            }
            token.ThrowIfCancellationRequested();
            current = processes.Find(installation.Computer, installation.Path);
            if (!Unchanged(approved, current)) return Deferred("После согласия запущена новая копия программы. Нужен новый запрос.");
            if (current.Any(p => !processes.ConfirmSession(installation.Computer, p))) return Deferred("Владелец сеанса изменился. Обновление отложено.");
            foreach (var process in current)
            {
                token.ThrowIfCancellationRequested();
                processes.Terminate(installation.Computer, process);
            }
            token.ThrowIfCancellationRequested();
            if (processes.Find(installation.Computer, installation.Path).Count != 0)
                return Deferred("Программа ещё работает или запущена повторно. Повторите обычное обновление.");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var detail = LoadUpdateErrors.Describe(ex);
            return Deferred("Закрытие не выполнено: " + detail.Detail);
        }
    }
    private static bool Unchanged(IReadOnlyList<LoadProcess> original, IReadOnlyList<LoadProcess> current) => current.All(p => original.Any(p.SameAs));
    private static LoadUpdateResult Deferred(string detail) => new(LoadUpdateState.Deferred, detail);
}
