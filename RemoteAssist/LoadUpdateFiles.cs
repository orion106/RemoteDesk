using System.Security.Cryptography;

namespace RemoteAssist;

public static class LoadUpdatePaths
{
    public static IReadOnlyList<string> Roots { get; } = Array.AsReadOnly(new[]
    {
        @"C:\Program Files\MMIS Lab\Plany", @"C:\Program Files (x86)\MMIS Lab\Plany",
        @"C:\Program Files(86)\MMIS Lab\Plany\ServiceUpdate"
    });
    public static string Share(string computer)
    {
        if (!Regex.IsMatch(computer, @"\A[a-zA-Z0-9][a-zA-Z0-9.\-]*\z") || computer.Length > 253)
            throw new InvalidOperationException("Некорректное имя удалённого компьютера.");
        return $@"\\{computer}\C$";
    }
    public static string Unc(string computer, string path)
    {
        var full = Path.GetFullPath(path);
        if (!Roots.Any(root => full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Путь находится вне каталогов нагрузки.");
        if (full.IndexOf(':', 2) >= 0) throw new InvalidOperationException("Альтернативные потоки файлов не поддерживаются.");
        return Share(computer) + full[2..];
    }
    public static void CheckChain(IUpdateFiles files, string computer, string localPath)
    {
        var full = Path.GetFullPath(localPath);
        _ = Unc(computer, full);
        var current = Share(computer);
        RejectLink(files.Attributes(current));
        foreach (var part in full[3..].Split('\\'))
        {
            current += "\\" + part;
            RejectLink(files.Attributes(current));
        }
    }
    public static void RejectLink(FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Путь содержит ссылку или junction; он пропущен.");
    }
}

public interface IUpdateFiles
{
    FileAttributes Attributes(string path);
    string[] Entries(string directory);
    string Version(string path);
    string Hash(string path);
    void Copy(string source, string target, CancellationToken token);
    void ProbeWritable(string path);
    void Replace(string stage, string target, string backup);
    void MoveNew(string source, string target);
    void Delete(string path);
}

public class PhysicalUpdateFiles : IUpdateFiles
{
    public virtual FileAttributes Attributes(string path) => File.GetAttributes(path);
    public virtual string[] Entries(string directory) => Directory.GetFileSystemEntries(directory);
    public virtual string Version(string path) => FileVersionInfo.GetVersionInfo(path).FileVersion ?? "Не указана";
    public virtual string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public virtual void Copy(string source, string target, CancellationToken token)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[128 * 1024];
        int count;
        while ((count = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
        output.Flush(true);
    }
    public virtual void ProbeWritable(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    public virtual void Replace(string stage, string target, string backup) => File.Replace(stage, target, backup);
    public virtual void MoveNew(string source, string target) => File.Move(source, target, false);
    public virtual void Delete(string path) => File.Delete(path);
}

public interface ILoadInstallationScanner { IReadOnlyList<LoadInstallation> Scan(string computer, CancellationToken token); }
public sealed class LoadInstallationScanner(IUpdateFiles files) : ILoadInstallationScanner
{
    public IReadOnlyList<LoadInstallation> Scan(string computer, CancellationToken token)
    {
        var results = new List<LoadInstallation>();
        try { _ = files.Attributes(LoadUpdatePaths.Share(computer)); }
        catch (Exception ex) { return [Diagnostic(computer, ex)]; }
        foreach (var root in LoadUpdatePaths.Roots)
        {
            token.ThrowIfCancellationRequested();
            try { LoadUpdatePaths.CheckChain(files, computer, root); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
            catch (Exception ex) { results.Add(Diagnostic(computer, ex, root)); continue; }
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var folder))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    LoadUpdatePaths.CheckChain(files, computer, folder);
                    foreach (var entry in files.Entries(LoadUpdatePaths.Unc(computer, folder)))
                    {
                        token.ThrowIfCancellationRequested();
                        var path = folder + "\\" + Path.GetFileName(entry);
                        try
                        {
                            var attributes = files.Attributes(entry);
                            LoadUpdatePaths.RejectLink(attributes);
                            if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(path);
                            else if (Path.GetFileName(path).Equals("Service.exe", StringComparison.OrdinalIgnoreCase))
                                results.Add(new(computer, path, files.Version(entry)));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { results.Add(Diagnostic(computer, ex, path)); }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { results.Add(Diagnostic(computer, ex, folder)); }
            }
        }
        if (results.Count == 0) results.Add(new(computer, "", "—", LoadUpdateState.NotInstalled, "Service.exe в проверенных каталогах не найден."));
        return results;
    }
    private static LoadInstallation Diagnostic(string computer, Exception exception, string path = "")
    {
        var result = LoadUpdateErrors.Describe(exception);
        return new(computer, path, "—", result.State, result.Detail);
    }
}

public interface ILoadFileUpdater
{
    LoadUpdateResult Update(LoadInstallation installation, LoadUpdatePackage package, CancellationToken token, Func<LoadUpdateResult?>? consent = null);
}

public sealed class LoadFileUpdater(IUpdateFiles files, IUpdateJournal journal) : ILoadFileUpdater
{
    public LoadUpdateResult Update(LoadInstallation installation, LoadUpdatePackage package, CancellationToken token, Func<LoadUpdateResult?>? consent = null)
    {
        string? stage = null, backup = null, originalHash = null, target = null;
        var attempted = false;
        var operation = Guid.NewGuid().ToString("N");
        LoadUpdateResult result;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!Path.GetFileName(installation.Path).Equals("Service.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Можно обновлять только Service.exe.");
            LoadUpdatePaths.CheckChain(files, installation.Computer, installation.Path);
            target = LoadUpdatePaths.Unc(installation.Computer, installation.Path);
            originalHash = files.Hash(target);
            if (originalHash == package.Hash)
            {
                var current = new LoadUpdateResult(LoadUpdateState.Current, "SHA-256 совпадает с выбранным обновлением.");
                try { journal.Write(new { Operation = operation, Phase = "VerifiedCurrent", installation.Computer, installation.Path, Hash = package.Hash, Result = current }); }
                catch { current = current with { Detail = current.Detail + " Не удалось записать результат в локальный журнал." }; }
                return current;
            }
            stage = target + ".remoteassist-" + operation + ".tmp";
            backup = target + $".backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-" + operation;
            files.Copy(package.SnapshotPath, stage, token);
            if (files.Hash(stage) != package.Hash) throw new InvalidOperationException("Контрольная сумма скопированного обновления не совпала. Оригинал сохранён.");
            token.ThrowIfCancellationRequested();
            // Durable intent is recorded before asking users to close or changing the executable.
            journal.Write(new { Operation = operation, Phase = "Prepared", installation.Computer, installation.Path, Stage = stage, Backup = backup, OriginalHash = originalHash, NewHash = package.Hash });
            var decision = consent?.Invoke();
            if (decision is not null) result = decision;
            else
            {
                token.ThrowIfCancellationRequested();
                LoadUpdatePaths.CheckChain(files, installation.Computer, installation.Path);
                if (files.Hash(target) != originalHash) throw new InvalidOperationException("Файл изменился после проверки. Выполните поиск заново.");
                files.ProbeWritable(target);
                token.ThrowIfCancellationRequested();
                // Once committed, verification/recovery must finish even if cancellation arrives.
                attempted = true;
                files.Replace(stage, target, backup);
                if (files.Hash(target) != package.Hash) throw new InvalidOperationException("Контрольная сумма после замены не совпала.");
                result = new(LoadUpdateState.Updated, "Замена проверена по SHA-256.", backup);
            }
        }
        catch (Exception ex)
        {
            result = attempted && target is not null && backup is not null && originalHash is not null
                ? Recover(installation, target, backup, originalHash, package.Hash, ex)
                : LoadUpdateErrors.Describe(ex);
        }
        finally
        {
            if (stage is not null)
            {
                try
                {
                    LoadUpdatePaths.CheckChain(files, installation.Computer, Path.GetDirectoryName(installation.Path)!);
                    files.Delete(stage);
                }
                catch { /* A disconnected host may retain this unique staging file; the journal records it. */ }
            }
        }
        try { journal.Write(new { Operation = operation, Phase = "Finished", installation.Computer, installation.Path, Result = result }); }
        catch { result = result with { Detail = result.Detail + " Не удалось записать итог в локальный журнал." }; }
        return result;
    }

    private LoadUpdateResult Recover(LoadInstallation installation, string target, string backup, string oldHash, string newHash, Exception error)
    {
        try
        {
            LoadUpdatePaths.CheckChain(files, installation.Computer, Path.GetDirectoryName(installation.Path)!);
            string? current;
            try { LoadUpdatePaths.RejectLink(files.Attributes(target)); current = files.Hash(target); }
            catch (FileNotFoundException) { current = null; }
            if (current == newHash) return new(LoadUpdateState.Updated, "Замена подтверждена повторной проверкой SHA-256.", backup);
            if (current == oldHash) return LoadUpdateErrors.Describe(error) with { Detail = LoadUpdateErrors.Describe(error).Detail + " Исходный файл не изменён." };
            LoadUpdatePaths.RejectLink(files.Attributes(backup));
            if (files.Hash(backup) != oldHash) throw new InvalidOperationException("Резервная копия не подтверждена.");
            var restore = target + ".restore-" + Guid.NewGuid().ToString("N");
            files.Copy(backup, restore, CancellationToken.None);
            try
            {
                if (files.Hash(restore) != oldHash) throw new InvalidOperationException("Копия для восстановления повреждена.");
                if (current is null) files.MoveNew(restore, target);
                else files.Replace(restore, target, target + ".failed-" + Guid.NewGuid().ToString("N"));
                if (files.Hash(target) != oldHash) throw new InvalidOperationException("Восстановление не подтверждено.");
            }
            finally { try { files.Delete(restore); } catch { } }
            return new(LoadUpdateState.Failed, "Проверка обновления не прошла. Исходный файл восстановлен и проверен.", backup);
        }
        catch
        {
            return new(LoadUpdateState.Uncertain, "Связь или проверка нарушена во время замены. Повторите обычное обновление для проверки SHA-256; при ошибке потребуется проверка резервной копии администратором.", backup);
        }
    }
}
