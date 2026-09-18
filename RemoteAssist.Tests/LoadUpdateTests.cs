using System.IO;
using System.Security;
using RemoteAssist;

namespace RemoteAssist.Tests;

internal static class LoadUpdateTests
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "updates"); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "Service.exe");
        File.Copy(Path.ChangeExtension(typeof(App).Assembly.Location, ".exe"), source);
        using var package = LoadUpdatePackage.Create(source, CancellationToken.None);
        check(package.Hash.Length == 64 && File.Exists(package.SnapshotPath), "Update package has a private SHA-256 snapshot.");
        File.WriteAllText(source, "changed source after selection");
        check(new PhysicalUpdateFiles().Hash(package.SnapshotPath) == package.Hash, "Changing the source cannot change the update snapshot.");
        var invalid = false;
        try { using var bad = LoadUpdatePackage.Create(source, CancellationToken.None); } catch { invalid = true; }
        check(invalid, "A non-PE Service.exe is rejected.");
        File.WriteAllBytes(source, []); invalid = false;
        try { using var bad = LoadUpdatePackage.Create(source, CancellationToken.None); } catch { invalid = true; }
        check(invalid, "An empty executable is rejected.");
        File.Copy(package.SnapshotPath, source, true);
        TestFiles(folder, package, check);
        TestConsent(check);
        await TestViewModel(folder, source, check);
    }

    private static void TestFiles(string folder, LoadUpdatePackage package, Action<bool, string> check)
    {
        const string host = "UPDATE-PC";
        var files = new MappedUpdateFiles(Path.Combine(folder, "remote"));
        var target = LoadUpdatePaths.Roots[0] + @"\Service.exe";
        var nested = LoadUpdatePaths.Roots[1] + @"\ServiceUpdate\Nested\Service.exe";
        files.Seed(host, target, "old executable"); files.Seed(host, nested, "old nested");
        files.Seed(host, LoadUpdatePaths.Roots[2] + @"\Service.exe", "legacy spelling");
        files.Seed(host, LoadUpdatePaths.Roots[0] + @"\Service.exe.config", "configuration");
        var scanner = new LoadInstallationScanner(files);
        var found = scanner.Scan(host, CancellationToken.None);
        check(found.Count == 3 && found.Any(f => f.Path == nested), "Scanner covers both Program Files trees, deep subfolders, and the literal legacy path.");
        files.Reparse = LoadUpdatePaths.Unc(host, LoadUpdatePaths.Roots[1] + @"\ServiceUpdate");
        found = scanner.Scan(host, CancellationToken.None);
        check(!found.Any(f => f.Path == nested) && found.Any(f => f.State == LoadUpdateState.Failed), "Scanner skips and reports junctions.");
        files.Reparse = null;
        files.Denied = LoadUpdatePaths.Unc(host, LoadUpdatePaths.Roots[1]);
        found = scanner.Scan(host, CancellationToken.None);
        check(found.Any(f => f.State == LoadUpdateState.AccessDenied) && found.Any(f => f.Path == target), "An inaccessible root is distinct from missing software; other roots are searched.");
        files.Denied = null;
        files.CreateHost("EMPTY");
        check(scanner.Scan("EMPTY", CancellationToken.None).Single().State == LoadUpdateState.NotInstalled, "An accessible host without the application is NotInstalled.");
        files.Offline = true;
        check(scanner.Scan(host, CancellationToken.None).Single().State == LoadUpdateState.Offline, "Offline hosts are distinct from missing software.");
        files.Offline = false;
        var invalid = false;
        try { LoadUpdatePaths.Unc(host, @"C:\Windows\Service.exe"); } catch (InvalidOperationException) { invalid = true; }
        check(invalid, "Paths outside allowed application roots are rejected.");
        invalid = false;
        try { LoadUpdatePaths.Unc(host, target + ":stream"); } catch (InvalidOperationException) { invalid = true; }
        check(invalid, "Alternate data streams are rejected.");
        invalid = false;
        try { LoadUpdatePaths.Share(@"HOST\elsewhere"); } catch (InvalidOperationException) { invalid = true; }
        check(invalid, "Hostnames cannot inject a share path.");

        var install = new LoadInstallation(host, target, "1.0");
        var journal = new MemoryJournal();
        var updater = new LoadFileUpdater(files, journal);
        var unc = LoadUpdatePaths.Unc(host, target);
        var oldHash = files.Hash(unc);
        files.FailCopy = true;
        var result = updater.Update(install, package, CancellationToken.None);
        check(result.State == LoadUpdateState.Failed && files.Hash(unc) == oldHash, "Interrupted copy leaves the original intact.");
        files.FailCopy = false;
        using (var held = new FileStream(files.Resolve(unc), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = updater.Update(install, package, CancellationToken.None);
            check(result.State == LoadUpdateState.Busy && files.Hash(unc) == oldHash, "A locked executable is Busy and stays unchanged.");
        }
        files.DenyWrite = true;
        result = updater.Update(install, package, CancellationToken.None);
        check(result.State == LoadUpdateState.AccessDenied && files.Hash(unc) == oldHash, "Access denied is not interpreted as a lock.");
        files.DenyWrite = false;
        result = updater.Update(install, package, CancellationToken.None, () => new(LoadUpdateState.Declined));
        check(result.State == LoadUpdateState.Declined && files.Hash(unc) == oldHash, "Rejected consent cannot replace a file.");
        using (var cancellation = new CancellationTokenSource())
        {
            result = updater.Update(install, package, cancellation.Token, () => { cancellation.Cancel(); return null; });
            check(result.State == LoadUpdateState.Cancelled && files.Hash(unc) == oldHash, "Cancellation before commit preserves the original.");
        }
        files.CorruptOnce = true;
        result = updater.Update(install, package, CancellationToken.None);
        check(result.State == LoadUpdateState.Failed && files.Hash(unc) == oldHash && result.Detail.Contains("восстановлен"), "Failed post-replace verification rolls back to the verified original.");
        check(result.BackupPath is not null && files.Hash(result.BackupPath) == oldHash, "Rollback retains a correct backup.");
        result = updater.Update(install, package, CancellationToken.None);
        check(result.State == LoadUpdateState.Updated && files.Hash(unc) == package.Hash, "Normal update commits the validated snapshot.");
        check(result.BackupPath is not null && files.Hash(result.BackupPath) == oldHash, "A successful update preserves the old file in a unique backup.");
        var count = files.Replacements;
        result = updater.Update(install, package, CancellationToken.None);
        check(result.State == LoadUpdateState.Current && files.Replacements == count, "Matching SHA-256 skips replacement.");
        check(File.ReadAllText(files.Resolve(LoadUpdatePaths.Unc(host, LoadUpdatePaths.Roots[0] + @"\Service.exe.config"))) == "configuration", "Application configuration is untouched.");
        files.Seed(host, target, "another old version");
        files.DisconnectOnce = true;
        result = updater.Update(install, package, CancellationToken.None);
        check(result.State == LoadUpdateState.Uncertain && result.BackupPath is not null, "A lost connection during commit reports an uncertain result with backup location.");
        files.Offline = false;
        result = updater.Update(install, package, CancellationToken.None);
        check(result.State == LoadUpdateState.Current, "A retry reconciles an uncertain successful replacement using its hash.");
        check(journal.Entries.Count > 6, "Replacement intent and results are journaled.");
        files.Seed(host, target, "preserve when journal fails"); oldHash = files.Hash(unc);
        var noJournal = new LoadFileUpdater(files, new BrokenJournal());
        var asked = false;
        result = noJournal.Update(install, package, CancellationToken.None, () => { asked = true; return null; });
        check(result.State == LoadUpdateState.Failed && !asked && files.Hash(unc) == oldHash, "Journal failure prevents user closure and commit.");
        using (var cancellation = new CancellationTokenSource())
        {
            files.AfterReplace = cancellation.Cancel;
            result = updater.Update(install, package, cancellation.Token);
            check(result.State == LoadUpdateState.Updated && files.Hash(unc) == package.Hash, "Cancellation arriving during commit does not interrupt verification.");
            files.AfterReplace = null;
        }
    }

    private static void TestConsent(Action<bool, string> check)
    {
        var target = LoadUpdatePaths.Roots[0] + @"\Service.exe";
        var install = new LoadInstallation("PC", target, "1");
        var p1 = new LoadProcess(10, target, "created-1", 1, @"DOMAIN\one", "S-1-5-21-1");
        var p2 = new LoadProcess(20, target, "created-2", 2, @"DOMAIN\two", "S-1-5-21-2");
        var processes = new TestProcesses { Live = [p1, p2], Answers = new([UpdateConsent.Yes, UpdateConsent.No]) };
        var coordinator = new LoadConsentCoordinator(processes, (_, _) => { });
        var result = coordinator.CloseWithConsent(install, CancellationToken.None);
        check(result?.State == LoadUpdateState.Declined && processes.Closed.Count == 0 && processes.Killed.Count == 0, "One refusal prevents closing all users of the same executable.");
        processes = new() { Live = [p1], Answers = new([UpdateConsent.Timeout]) };
        result = new LoadConsentCoordinator(processes).CloseWithConsent(install, CancellationToken.None);
        check(result?.State == LoadUpdateState.TimedOut && processes.Closed.Count == 0, "Prompt timeout does not close the program.");
        processes = new() { Live = [p1], Answers = new([UpdateConsent.Unavailable]) };
        result = new LoadConsentCoordinator(processes).CloseWithConsent(install, CancellationToken.None);
        check(result?.State == LoadUpdateState.Deferred && processes.Closed.Count == 0, "Failed notification cannot authorize termination.");
        processes = new() { Live = [p1, p2], Answers = new([UpdateConsent.Yes, UpdateConsent.Yes]) };
        var waited = TimeSpan.Zero;
        result = new LoadConsentCoordinator(processes, (delay, _) => waited = delay).CloseWithConsent(install, CancellationToken.None);
        check(result is null && waited == TimeSpan.FromSeconds(15) && processes.Closed.Count == 2 && processes.Killed.Count == 2, "All consented processes get graceful close followed by a 15-second wait and force termination.");
        processes = new() { Live = [p1], Answers = new([UpdateConsent.Yes]), ExitGracefully = true };
        result = new LoadConsentCoordinator(processes, (_, _) => { }).CloseWithConsent(install, CancellationToken.None);
        check(result is null && processes.Killed.Count == 0, "Gracefully closed processes are never force terminated.");
        processes = new() { Live = [p1], Answers = new([UpdateConsent.Yes]) };
        processes.AfterAsk = () => processes.Live[0] = p1 with { Created = "replacement-pid" };
        result = new LoadConsentCoordinator(processes, (_, _) => { }).CloseWithConsent(install, CancellationToken.None);
        check(result?.State == LoadUpdateState.Deferred && processes.Closed.Count == 0, "A reused PID requires new consent.");
        processes = new() { Live = [p1], Answers = new([UpdateConsent.Yes]) };
        result = new LoadConsentCoordinator(processes, (_, _) => processes.Live[0] = p1 with { SessionId = 3 }).CloseWithConsent(install, CancellationToken.None);
        check(result?.State == LoadUpdateState.Deferred && processes.Killed.Count == 0, "Session changes during the grace period prevent force termination.");
        processes = new() { Live = [p1], Answers = new([UpdateConsent.Yes]) };
        processes.AfterAsk = () => processes.Live.Add(p2);
        result = new LoadConsentCoordinator(processes, (_, _) => { }).CloseWithConsent(install, CancellationToken.None);
        check(result?.State == LoadUpdateState.Deferred && processes.Closed.Count == 0, "A newly launched process requires consent before any close.");
        processes = new() { Live = [p1 with { OwnerSid = "" }] };
        result = new LoadConsentCoordinator(processes).CloseWithConsent(install, CancellationToken.None);
        check(result?.State == LoadUpdateState.Deferred && processes.Asked == 0, "Unconfirmed ownership cannot be prompted or killed.");
        processes = new() { Live = [p1], SessionValid = false };
        result = new LoadConsentCoordinator(processes).CloseWithConsent(install, CancellationToken.None);
        check(result?.State == LoadUpdateState.Deferred && processes.Asked == 0, "Disconnected or changed sessions defer updates.");
        using (var cancellation = new CancellationTokenSource())
        {
            processes = new() { Live = [p1], Answers = new([UpdateConsent.Yes]), AfterAsk = cancellation.Cancel };
            var cancelled = false;
            try { new LoadConsentCoordinator(processes).CloseWithConsent(install, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && processes.Closed.Count == 0 && processes.Killed.Count == 0, "Late Yes after cancellation cannot close a process.");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            processes = new() { Live = [p1], Answers = new([UpdateConsent.Yes]) };
            var cancelled = false;
            try { new LoadConsentCoordinator(processes, (_, _) => cancellation.Cancel()).CloseWithConsent(install, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && processes.Killed.Count == 0, "Cancellation during the grace period prevents force termination.");
        }
        var outsider = p2 with { Path = @"C:\Other\Service.exe" };
        processes = new() { Live = [p1, outsider], Answers = new([UpdateConsent.Yes]) };
        result = new LoadConsentCoordinator(processes, (_, _) => { }).CloseWithConsent(install, CancellationToken.None);
        check(result is null && processes.Live.Single() == outsider && processes.Killed.SequenceEqual([p1.Id]), "Another executable named Service.exe is never closed.");
    }

    private static async Task TestViewModel(string folder, string source, Action<bool, string> check)
    {
        var settingsFile = Path.Combine(folder, "settings.json");
        File.WriteAllText(settingsFile, "{\"ComputerMasks\":[\"HOME-*\"],\"Theme\":\"Светлая\"}");
        var settings = UserSettings.Load(settingsFile);
        check(settings.LoadUpdateMasks.Length == 0 && settings.Theme == "Светлая", "Old settings migrate with empty update masks and preserved theme.");
        using var store = new MemoryStore();
        using var config = new DirectoryConfiguration(settings, store, settingsFile);
        using var mainSettings = new SettingsViewModel(config, new FakeDirectory());
        var directory = new FakeDirectory { Count = 20, IncludeUnmatchedComputer = true };
        var factory = new TestUpdateSessionFactory();
        using var vm = new LoadUpdateViewModel(directory, config, factory);
        await vm.ScanAsync();
        check(vm.Results.Count == 0 && factory.OpenCount == 0, "An empty update scope cannot scan computers.");
        mainSettings.LoadUpdateMasksText = "DC*-*";
        await vm.ScanAsync();
        check(vm.Results.Count == 0 && factory.OpenCount == 0, "Unsaved masks in the settings draft cannot start a scan.");
        mainSettings.SaveCommand.Execute(null);
        check(config.Settings.LoadUpdateMasks.SequenceEqual(["DC*-*"]) && config.Settings.ComputerMasks.SequenceEqual(["HOME-*"]), "Central settings save independent desktop and update scopes.");
        check(UserSettings.Load(settingsFile).LoadUpdateMasks.SequenceEqual(["DC*-*"]) && vm.MaskSummary == "DC*-*", "Saved update masks persist and refresh the operation page summary.");
        mainSettings.LoadUpdateMasksText = "OTHER-*";
        mainSettings.MasksText = "CHANGED-*";
        mainSettings.Draft.Theme = "Тёмная";
        mainSettings.NavigateCategoryCommand.Execute("Оформление");
        mainSettings.CancelCommand.Execute(null);
        check(mainSettings.LoadUpdateMasksText == "DC*-*" && mainSettings.MasksText == "HOME-*" && mainSettings.Draft.Theme == "Светлая", "Cancel restores pending changes from every settings category.");
        mainSettings.LoadUpdateMasksText = "INVALID?";
        mainSettings.Draft.Theme = "Тёмная";
        mainSettings.SaveCommand.Execute(null);
        check(config.Settings.Theme == "Светлая" && config.Settings.LoadUpdateMasks.SequenceEqual(["DC*-*"]) && mainSettings.Category == "Обновление нагрузки", "Invalid update masks prevent all draft changes and reveal the relevant category.");
        mainSettings.CancelCommand.Execute(null);
        await vm.ScanAsync();
        check(vm.Results.Count == 20 && !factory.Hosts.Contains("OTHER-01"), "Update scan uses its own masks, including a defensive filter.");
        check(factory.MaxConcurrent <= 4 && factory.MaxConcurrent > 1, "Remote update work has at most four concurrent hosts.");
        await vm.SelectPackageAsync(source);
        check(vm.UpdateCommand.CanExecute(null) && !vm.ForceRequestCommand.CanExecute(null), "A package enables normal update but not premature consent requests.");
        await vm.UpdateAsync(false);
        check(vm.Results.All(r => r.Result.State == LoadUpdateState.Busy) && vm.ForceRequestCommand.CanExecute(null), "Normal Busy results enable consent requests.");
        vm.SelectNoneCommand.Execute(null);
        check(!vm.UpdateCommand.CanExecute(null) && !vm.ForceRequestCommand.CanExecute(null), "No selected rows disable update commands.");
        vm.SelectAllCommand.Execute(null);
        await vm.UpdateAsync(true);
        check(factory.Forced == 20 && vm.Results.All(r => r.Result.State == LoadUpdateState.Updated), "Consent update operates on selected busy installations.");
        check(!vm.ForceRequestCommand.CanExecute(null), "Successfully updated files cannot be force-requested again.");
        await vm.UpdateAsync(false);
        await vm.SelectPackageAsync(source);
        check(!vm.ForceRequestCommand.CanExecute(null), "Changing the package invalidates the previous busy batch.");
        factory.Delay = 80;
        var work = vm.ScanAsync();
        await Task.Delay(15);
        mainSettings.LoadUpdateMasksText = "OTHER-*";
        mainSettings.SaveCommand.Execute(null);
        await work;
        check(!vm.IsBusy && vm.Results.Count == 0, "Settings changes cancel old scans and discard stale results.");
    }
}

internal sealed class MemoryJournal : IUpdateJournal
{
    public List<object> Entries { get; } = [];
    public void Write(object entry) => Entries.Add(entry);
}
internal sealed class BrokenJournal : IUpdateJournal { public void Write(object entry) => throw new IOException("Synthetic journal failure"); }

internal sealed class MappedUpdateFiles(string root) : IUpdateFiles
{
    private readonly PhysicalUpdateFiles _physical = new();
    public string? Reparse, Denied;
    public bool Offline, DenyWrite, FailCopy, CorruptOnce, DisconnectOnce;
    public int Replacements;
    public Action? AfterReplace;
    public string Resolve(string path) => path.StartsWith(@"\\") ? Path.Combine(root, path[2..].Replace(@"\C$", "")) : path;
    public void CreateHost(string host) => Directory.CreateDirectory(Resolve(LoadUpdatePaths.Share(host)));
    public void Seed(string host, string path, string text)
    {
        var target = Resolve(LoadUpdatePaths.Unc(host, path)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, text);
    }
    private void Guard(string path)
    {
        if (Offline) throw new IOException("Synthetic network loss", unchecked((int)0x80070040));
        if (path == Denied) throw new UnauthorizedAccessException();
    }
    public FileAttributes Attributes(string path) { Guard(path); return path == Reparse ? FileAttributes.Directory | FileAttributes.ReparsePoint : _physical.Attributes(Resolve(path)); }
    public string[] Entries(string directory) { Guard(directory); return _physical.Entries(Resolve(directory)).Select(p => directory + "\\" + Path.GetFileName(p)).ToArray(); }
    public string Version(string path) { Guard(path); return "1.0"; }
    public string Hash(string path) { Guard(path); return _physical.Hash(Resolve(path)); }
    public void Copy(string source, string target, CancellationToken token)
    {
        Guard(target);
        if (FailCopy) { File.WriteAllText(Resolve(target), "partial"); throw new IOException("Synthetic interrupted copy"); }
        _physical.Copy(Resolve(source), Resolve(target), token);
    }
    public void ProbeWritable(string path) { Guard(path); if (DenyWrite) throw new UnauthorizedAccessException(); _physical.ProbeWritable(Resolve(path)); }
    public void Replace(string stage, string target, string backup)
    {
        Guard(target); _physical.Replace(Resolve(stage), Resolve(target), Resolve(backup)); Replacements++;
        if (CorruptOnce) { CorruptOnce = false; File.WriteAllText(Resolve(target), "corrupt replacement"); }
        if (DisconnectOnce) { DisconnectOnce = false; Offline = true; }
        AfterReplace?.Invoke();
    }
    public void MoveNew(string source, string target) { Guard(target); _physical.MoveNew(Resolve(source), Resolve(target)); }
    public void Delete(string path) { Guard(path); _physical.Delete(Resolve(path)); }
}

internal sealed class TestProcesses : ILoadProcesses
{
    public List<LoadProcess> Live = [];
    public Queue<UpdateConsent> Answers = new();
    public List<uint> Closed = [], Killed = [];
    public int Asked;
    public bool SessionValid = true, ExitGracefully;
    public Action? AfterAsk;
    public IReadOnlyList<LoadProcess> Find(string computer, string path) => Live.Where(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
    public bool ConfirmSession(string computer, LoadProcess process) => SessionValid;
    public UpdateConsent Ask(string computer, LoadProcess process) { Asked++; var answer = Answers.Dequeue(); AfterAsk?.Invoke(); return answer; }
    public void RequestClose(string computer, LoadProcess process) { Closed.Add(process.Id); if (ExitGracefully) Live.RemoveAll(p => p.SameAs(process)); }
    public void Terminate(string computer, LoadProcess process) { Killed.Add(process.Id); Live.RemoveAll(p => p.SameAs(process)); }
}

internal sealed class TestUpdateSessionFactory : IRemoteUpdateSessionFactory
{
    public int OpenCount, MaxConcurrent, Concurrent, Forced, Delay = 10;
    public List<string> Hosts = [];
    public IRemoteUpdateSession Open(DirectoryConfiguration configuration) { OpenCount++; return new Session(this); }
    private sealed class Session(TestUpdateSessionFactory owner) : IRemoteUpdateSession
    {
        public async Task<IReadOnlyList<LoadInstallation>> ScanAsync(string computer, CancellationToken token)
        {
            owner.Hosts.Add(computer); owner.MaxConcurrent = Math.Max(owner.MaxConcurrent, ++owner.Concurrent);
            try { await Task.Delay(owner.Delay, token); return [new(computer, LoadUpdatePaths.Roots[0] + @"\Service.exe", "1.0")]; }
            finally { owner.Concurrent--; }
        }
        public async Task<LoadUpdateResult> UpdateAsync(LoadInstallation installation, LoadUpdatePackage package, bool requestConsent, CancellationToken token)
        {
            owner.MaxConcurrent = Math.Max(owner.MaxConcurrent, ++owner.Concurrent);
            try { await Task.Delay(owner.Delay, token); if (requestConsent) owner.Forced++; return new(requestConsent ? LoadUpdateState.Updated : LoadUpdateState.Busy); }
            finally { owner.Concurrent--; }
        }
        public void Dispose() { }
    }
}
