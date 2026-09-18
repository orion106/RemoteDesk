using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using RemoteAssist;

namespace RemoteAssist.Tests;

internal static class LabTests
{
    public static async Task Run(string root, Action<bool,string> check)
    {
        var directory = Path.Combine(root, "lab"); Directory.CreateDirectory(directory);
        var win = Computer("lab-01"); win.LocalPlacement = true; win.Room = "301"; win.Seat = "7";
        win.Systems[0].LastSnapshot = new() { Os = LabOs.Windows, Version = "Windows 11", Reachable = true };
        var linux = Computer("lab-01-astra"); linux.GlpiIds = ["glpi#2"]; linux.Systems = [new() { Os = LabOs.Astra, Address = "lab-01", Description = "Astra 1.8" }];
        var merged = MachineIdentity.MergeInventory([win], [linux]);
        check(merged.Count == 1 && merged[0].Systems.Count == 2 && merged[0].Room == "301" && merged[0].Seat == "7", "Dual boot inventory merges without losing local classroom placement.");
        check(merged[0].Systems[0].LastSnapshot?.Version == "Windows 11", "Inactive Windows snapshot survives Astra inventory.");
        var other = Computer("other"); other.Uuid = Guid.NewGuid().ToString(); other.GlpiIds = ["glpi#3"];
        check(MachineIdentity.MergeInventory([win], [other]).Count == 2, "Conflicting UUID prevents a serial-only merge.");
        var changed = win.Copy(); changed.Uuid = Guid.NewGuid().ToString();
        var changedResult = MachineIdentity.MergeInventory([win], [changed]);
        check(changedResult[0].Uuid == win.Uuid && changedResult[0].InventoryNote.Contains("изменился"), "GLPI ID reuse does not silently reassign physical identity.");
        check(!MachineIdentity.Valid("00000000-0000-0000-0000-000000000000") && !MachineIdentity.Valid("To be filled by O.E.M."), "Placeholder hardware identities are rejected.");
        check(!MachineIdentity.Matches(win, new() { Reachable = true, Os = LabOs.Windows, Uuid = Guid.NewGuid().ToString(), Serial = win.Serial }), "Changed host behind an IP cannot receive commands.");
        var store = new LabStore(Path.Combine(directory, "test.db")); store.Save("computer", win.Id, win);
        check(new LabStore(Path.Combine(directory, "test.db")).Load<LabComputer>("computer").Single().Seat == "7", "SQLite persists physical computer and placement.");
        store.ReplaceComputers(merged); check(store.Load<LabComputer>("computer").Count == 1, "Inventory replacement is transactional.");
        TestProfiles(check);
        var csvPath=Path.Combine(directory,"glpi.csv");
        File.WriteAllText(csvPath,"Имя;Расположение;UUID;Серийный номер;IP;Операционная система\n\"dc415-02\";\"Корпус; 415\";E5F3A127-999A-4DD8-ABCD-012345678901;LABSERIAL-01;192.0.2.12;Astra Linux 1.8\n",new UTF8Encoding(true));
        var imported=InventoryImport.Csv(csvPath);
        check(imported.Count==1 && imported[0].Name=="dc415-02" && imported[0].Room=="Корпус; 415" && imported[0].Systems[0].Os==LabOs.Astra,"CSV GLPI imports quoted Russian headings and fields without API credentials.");
        check(MachineIdentity.MergeInventory(imported,InventoryImport.Csv(csvPath)).Count==1,"Repeated CSV import does not duplicate a physical computer.");
        File.AppendAllText(csvPath,"dc415-02;415;F5F3A127-999A-4DD8-ABCD-012345678902;LABSERIAL-02;192.0.2.13;Windows 11\n");
        check(MachineIdentity.MergeInventory([], InventoryImport.Csv(csvPath)).Count == 2, "Same CSV hostname with different hardware remains two computers.");
        var adImported=InventoryImport.FromAd(new("dc415-02","dc415-02.example.test","CN=dc415-02,DC=example,DC=test"));
        check(adImported.Systems.Single().Address=="dc415-02.example.test" && !MachineIdentity.Valid(adImported.Uuid),"AD import preserves FQDN and requires hardware verification.");
        var lease = await MachineMutationGate.AcquireComputerAsync(win, CancellationToken.None);
        var blocked = MachineMutationGate.AcquireHostAsync("LAB-01.example.test", CancellationToken.None);
        await Task.Delay(25); check(!blocked.IsCompleted, "Legacy updater and lab tasks share a host/physical-machine lock.");
        lease.Dispose(); using (await blocked) { }
        check(blocked.IsCompletedSuccessfully, "Shared lock releases after operation.");
        using var secretStore = new TestSecrets();
        using var config = new DirectoryConfiguration(new UserSettings(), secretStore, Path.Combine(directory, "settings.json"));
        var fake = new FakeAdapter();
        using var runtime = new LabRuntime(store, config, [fake], (_,_,_) => Task.FromResult(true));
        runtime.Computers.Clear(); runtime.Computers.AddRange(Enumerable.Range(0, 100).Select(i => { var p=Computer("PC-"+i);p.Uuid=Guid.NewGuid().ToString();p.Serial="SERIAL-"+i;return p; }));
        await runtime.PollAsync(CancellationToken.None);
        check(fake.MaxProbe is > 1 and <= 8 && runtime.Snapshots.Count == 100, "100-PC monitoring obeys eight-probe concurrency bound.");
        fake.Fail = true; var pc = runtime.Computers[0];
        await runtime.ProbeAsync(pc, CancellationToken.None); await runtime.ProbeAsync(pc, CancellationToken.None);
        check(!runtime.Alerts.Any(a => a.ComputerId == pc.Id), "Two failed scans do not generate an availability alarm.");
        await runtime.ProbeAsync(pc, CancellationToken.None); await runtime.ProbeAsync(pc, CancellationToken.None);
        check(runtime.Alerts.Count(a => a.ComputerId == pc.Id && a.ResolvedAt is null) == 1, "Repeated availability alarms are deduplicated.");
        fake.Fail = false; await runtime.ProbeAsync(pc, CancellationToken.None);
        check(runtime.Alerts.Single(a => a.ComputerId == pc.Id).ResolvedAt is not null, "Recovery resolves availability alarm.");
        fake.LowDisk = true; await runtime.ProbeAsync(pc, CancellationToken.None);
        check(runtime.Alerts.Any(a => a.Key.Contains("disk:") && a.ResolvedAt is null), "Low disk threshold creates a persistent alert.");
        fake.LowDisk = false; await runtime.ProbeAsync(pc, CancellationToken.None);
        check(runtime.Alerts.Where(a => a.Key.Contains("disk:")).All(a => a.ResolvedAt is not null), "Healthy disk resolves only confirmed platform alert.");
        var packagePath = Path.Combine(directory,"fixture.msi"); File.WriteAllText(packagePath,"Synthetic fixture, never executed");
        var pkg = new SoftwarePackage { Name="Test",Version="1.2",Detection=@"C:\Test\app.exe",Source=packagePath,Sha256=LabWire.Sha(packagePath),Os=LabOs.Windows,Kind="MSI" };
        await Task.WhenAll(runtime.Computers.Take(12).Select(p => runtime.EnqueueAsync(p, LabAction.Install, new PackagePayload(pkg), CancellationToken.None)));
        check(fake.MaxRun is > 1 and <= 4 && runtime.Jobs.Count(x=>x.State==JobState.Succeeded)==12, "Bulk install obeys four-computer limit and journals outcomes.");
        fake.RunningByPc.Clear(); fake.Overlap = false;
        await Task.WhenAll(runtime.EnqueueAsync(pc,LabAction.Install,new PackagePayload(pkg),CancellationToken.None),runtime.EnqueueAsync(pc,LabAction.Install,new PackagePayload(pkg),CancellationToken.None));
        check(!fake.Overlap, "Two jobs on the same physical computer never overlap.");
        var wrong = JsonSerializer.Deserialize<SoftwarePackage>(JsonSerializer.Serialize(pkg))!; wrong.Os=LabOs.Astra;wrong.Kind="DEB";wrong.Detection="test";
        var before=fake.Calls; await runtime.EnqueueAsync(pc,LabAction.Install,new PackagePayload(wrong),CancellationToken.None);
        check(fake.Calls==before && runtime.Jobs.Last().State==JobState.Deferred && runtime.Jobs.Last().Detail.Contains("Astra"), "Package for inactive OS is deferred without an implicit reboot.");
        fake.Result=new(JobState.RebootRequired,"3010"); await runtime.EnqueueAsync(pc,LabAction.Install,new PackagePayload(pkg),CancellationToken.None);
        check(runtime.Jobs.Last().State==JobState.RebootRequired,"Installer reboot-required result remains explicit.");
        fake.Result=new(JobState.Uncertain,"connection lost");await runtime.EnqueueAsync(pc,LabAction.Install,new PackagePayload(pkg),CancellationToken.None);
        var uncertain=runtime.Jobs.Last(); before=fake.Calls; fake.Reconciled=new() { State="Succeeded",Detail="confirmed" };
        var rejected=false;try{using var unsafeLease=await MachineMutationGate.AcquireHostAsync(pc.Name,CancellationToken.None);}catch(InvalidOperationException){rejected=true;}
        check(rejected,"Unconfirmed lab mutation also blocks the legacy updater.");
        await runtime.ReconcileAsync(uncertain,CancellationToken.None);
        check(fake.Calls==before && uncertain.State==JobState.Succeeded,"Reconciliation reads outcome without rerunning installer.");
        await runtime.EnqueueAsync(pc, LabAction.Shutdown, null, CancellationToken.None);
        var shutdown = runtime.Jobs.Last(); rejected = false;
        try { using var unsafeLease = await MachineMutationGate.AcquireHostAsync(pc.Name, CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && shutdown.State == JobState.Uncertain, "A possibly pending shutdown blocks further machine mutations.");
        fake.BootId = "boot2"; await runtime.ReconcileAsync(shutdown, CancellationToken.None);
        using (await MachineMutationGate.AcquireHostAsync(pc.Name, CancellationToken.None)) { }
        check(shutdown.State == JobState.Failed, "New confirmed boot clears a pending old shutdown without claiming historical power-off success.");
        fake.Mismatch=true;before=fake.Calls;await runtime.EnqueueAsync(pc,LabAction.Install,new PackagePayload(pkg),CancellationToken.None);
        check(fake.Calls==before && runtime.Jobs.Last().State==JobState.Deferred,"Identity change between inventory and job prevents mutation.");fake.Mismatch=false;
        using(var cancel=new CancellationTokenSource()) { cancel.Cancel(); before=fake.Calls; await runtime.EnqueueAsync(pc,LabAction.Install,new PackagePayload(pkg),cancel.Token); check(fake.Calls==before && runtime.Jobs.Last().State==JobState.Cancelled,"Pre-dispatch cancellation never runs a remote mutation."); }
        store.Save("job","interrupted",new AdminJob {Id="interrupted",State=JobState.Running});
        store.Save("schedule","missed",new LabSchedule {Id="missed",NextRun=DateTimeOffset.Now.AddHours(-1),Enabled=true,Weekly=false});
        using(var restart = new LabRuntime(store,config,[fake],(_,_,_)=>Task.FromResult(true)))
        {
            check(restart.Jobs.Single(x=>x.Id=="interrupted").State==JobState.Uncertain,"Restart marks interrupted jobs unconfirmed.");
            check(!restart.Schedules.Single(x=>x.Id=="missed").Enabled && restart.Jobs.Any(x=>x.State==JobState.Missed),"Missed schedules are logged and never replayed on startup.");
        }
        var packet=WakeOnLan.Packet("02:11:22:33:44:55");check(packet.Length==102 && packet.Take(6).All(x=>x==255) && packet.Skip(6).Take(6).SequenceEqual(packet.Skip(96)),"Wake-on-LAN packet contains 16 repetitions of the physical MAC.");
        var invalid=false;try{new LabOptions{StudentMasks="*"}.Validate();}catch(InvalidOperationException){invalid=true;}check(invalid,"Universal student mask rejected.");
        invalid=false;try{new LabOptions{GlpiUrl="http://glpi.test"}.Validate();}catch(InvalidOperationException){invalid=true;}check(invalid,"GLPI refuses cleartext transport of API tokens.");
        var json=LabWire.Request(new(){Uuid="00000000-0000-0000-0000-000000000000",Serial="VALID123"},"Probe",new{},new());check(!json.Contains("00000000-"),"Invalid identity fields are not forwarded as trusted constraints.");
        await TestGlpi(secretStore,check);
    }
    private static void TestProfiles(Action<bool,string> check)
    {
        var options=new LabOptions{StudentMasks="student*"};
        StudentProfile P() => new(){Sid="S-1-5-21-1-2-3-1001",Login=@"DOMAIN\student01",Path=@"C:\Users\student01",Administrator=false,LocalPathSafe=true};
        check(ProfilePolicy.Reason(P(),options)=="","Explicit student mask allows an unloaded ordinary profile.");
        var profile=P();profile.Login=@"PC\admin";check(ProfilePolicy.Reason(profile,options).Length>0,"admin always protected.");
        profile=P();profile.Sid="S-1-5-21-1-2-3-500";check(ProfilePolicy.Reason(profile,options).Length>0,"Renamed built-in administrator protected by SID.");
        profile=P();profile.Administrator=true;check(ProfilePolicy.Reason(profile,options).Contains("Администратор"),"Nested administrator membership excludes profile.");
        profile=P();profile.Administrator=null;check(ProfilePolicy.Reason(profile,options).Contains("не подтверждены"),"Unresolved administrator membership fails closed.");
        profile=P();profile.Loaded=true;check(ProfilePolicy.Reason(profile,options).Contains("используется"),"Loaded student profile excluded.");
        profile=P();profile.Special=true;check(ProfilePolicy.Reason(profile,options).Length>0,"Service/system profile excluded.");
        profile=P();profile.LocalPathSafe=false;check(ProfilePolicy.Reason(profile,options).Length>0,"Redirected, linked, or unsafe profile root excluded.");
        check(ProfilePolicy.Reason(P(),new()).Length>0,"No configured student rules means no deletions.");
        profile=P();profile.Login=@"DOMAIN\teacher01";check(ProfilePolicy.Reason(profile,options).Length>0,"Teacher absent from allowlist is preserved.");
        check(ProfilePolicy.Matches("student(1)","student(1)") && !ProfilePolicy.Matches("student1","student(1)"),"Student masks treat regex punctuation literally.");
    }
    private static async Task TestGlpi(TestSecrets secrets,Action<bool,string> check)
    {
        var options=new LabOptions{GlpiUrl="https://glpi.example.test"};using var secret=new SecureString();foreach(var c in "test-token")secret.AppendChar(c);
        new LabSecrets(secrets).Save("glpi-user",options.GlpiUrl,secret);
        using var handler=new GlpiHandler();var source=new GlpiInventorySource(options,new LabSecrets(secrets),handler);
        var computers=await source.ReadAsync(CancellationToken.None);
        check(computers.Count==1 && computers[0].Room=="Building > 301" && computers[0].Systems.Single().Os==LabOs.Windows,"GLPI classic API imports location and OS relations.");
        check(handler.Methods.All(x=>x==HttpMethod.Get),"GLPI integration never mutates inventory.");
        check(handler.Ended && handler.Authorized,"GLPI session uses headers and is explicitly closed.");
    }
    public static LabComputer Computer(string name) => new(){Name=name,Uuid="E5F3A127-999A-4DD8-ABCD-012345678901",Serial="LABSERIAL-01",GlpiIds=["glpi#1"],Systems=[new(){Os=LabOs.Windows,Address=name}]};
    public sealed class TestSecrets:ICredentialStore,IDisposable
    {
        private readonly Dictionary<string,SecureString> _values=[];
        public SecureString? Read(string key)=>_values.TryGetValue(key,out var value)?value.Copy():null;
        public void Write(string key,string user,SecureString value){Delete(key);_values[key]=value.Copy();}
        public void Delete(string key){if(_values.Remove(key,out var old))old.Dispose();}
        public void Dispose(){foreach(var value in _values.Values)value.Dispose();}
    }
    public sealed class FakeAdapter:ISystemAdapter
    {
        public LabOs Os=>LabOs.Windows;
        private int _probes,_runs;
        public int MaxProbe,MaxRun,Calls;
        public bool Fail,LowDisk,Mismatch,Overlap;
        public string BootId = "boot1";
        public Dictionary<string,int> RunningByPc=[];
        public OperationResult Result=new(JobState.Succeeded,"done");
        public RemoteReply? Reconciled;
        public async Task<MachineSnapshot> ProbeAsync(LabComputer pc,OsEndpoint endpoint,CancellationToken token)
        {
            var count=Interlocked.Increment(ref _probes);MaxProbe=Math.Max(MaxProbe,count);
            try{await Task.Delay(3,token);if(Fail)throw new UnauthorizedAccessException();return new(){Os=Os,Address=endpoint.Address,Uuid=Mismatch?Guid.NewGuid().ToString():pc.Uuid,Serial=pc.Serial,Reachable=true,BootId=BootId,Disks=[new("C:",1000,LowDisk?50:500)]};}
            finally{Interlocked.Decrement(ref _probes);}
        }
        public async Task<RemoteReply> RunAsync(LabComputer pc,OsEndpoint ep,string operation,object payload,string requestId,CancellationToken token)
        {
            Interlocked.Increment(ref Calls);var count=Interlocked.Increment(ref _runs);MaxRun=Math.Max(MaxRun,count);
            lock(RunningByPc){if(RunningByPc.GetValueOrDefault(pc.Id)>0)Overlap=true;RunningByPc[pc.Id]=RunningByPc.GetValueOrDefault(pc.Id)+1;}
            try{await Task.Delay(25,token);return new(){State=Result.State.ToString(),Detail=Result.Detail};}
            finally{Interlocked.Decrement(ref _runs);lock(RunningByPc)RunningByPc[pc.Id]--;}
        }
        public Task<RemoteReply?> ReconcileAsync(OsEndpoint ep,string id,CancellationToken token)=>Task.FromResult(Reconciled);
    }
    private sealed class GlpiHandler:HttpMessageHandler
    {
        public List<HttpMethod> Methods=[];public bool Ended,Authorized;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            lock(Methods)Methods.Add(request.Method);var path=request.RequestUri!.AbsolutePath.Replace("/apirest.php/","");
            if(path=="initSession")Authorized=request.Headers.TryGetValues("Authorization",out var a)&&a.Single()=="user_token test-token";
            if(path=="killSession")Ended=true;
            var body=path switch{
                "initSession"=>"{\"session_token\":\"test-session\"}","killSession"=>"true",
                "Computer"=>"[{\"id\":1,\"name\":\"PC-01\",\"uuid\":\"E5F3A127-999A-4DD8-ABCD-012345678901\",\"serial\":\"SER12345\",\"locations_id\":3}]",
                "Location/3"=>"{\"completename\":\"Building > 301\"}","Computer/1/NetworkPort"=>"[]",
                "Computer/1/Item_OperatingSystem"=>"[{\"operatingsystems_id\":4,\"operatingsystemversions_id\":5}]",
                "OperatingSystem/4"=>"{\"name\":\"Windows\"}","OperatingSystemVersion/5"=>"{\"name\":\"11\"}",_=>throw new InvalidOperationException("Unexpected GLPI request: "+path)};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
}
