using System.IO;
using RemoteAssist;

namespace RemoteAssist.Tests;

internal static class InventoryLinkTests
{
    public static Task Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "inventory-links"); Directory.CreateDirectory(directory);
        using var credentials = new LabTests.TestSecrets();
        using var configuration = new DirectoryConfiguration(new UserSettings(), credentials, Path.Combine(directory, "settings.json"));
        var store = new LabStore(Path.Combine(directory, "administrator-a.db"));
        var otherStore = new LabStore(Path.Combine(directory, "administrator-b.db"));
        var original = LabTests.Computer("lab-shared"); original.Room = "Старый кабинет"; original.Seat = "1";
        var otherLocal = original.Copy(); otherLocal.Id = Guid.NewGuid().ToString("N");
        store.Save("computer", original.Id, original); otherStore.Save("computer", otherLocal.Id, otherLocal);
        using var runtime = new LabRuntime(store, configuration, []);
        using var otherRuntime = new LabRuntime(otherStore, configuration, []);
        using var links = new InventoryLinkService(store, runtime);
        using var otherLinks = new InventoryLinkService(otherStore, otherRuntime);
        var asset = new InventoryPlacementProjection("asset-a", "201Д", "001", Uuid: original.Uuid, Serial: original.Serial);
        var secondAsset = new InventoryPlacementProjection("asset-b", "301Д", "002");
        links.ApplySnapshot("nas-main", [asset, secondAsset]); otherLinks.ApplySnapshot("nas-main", [asset, secondAsset]);
        check(links.SuggestedAssets(original.Id).Single().AssetId == asset.AssetId, "Hardware IDs suggest an existing inventory asset without creating or binding it.");
        check(links.GetLink(original.Id) is null, "Inventory refresh alone never automatically links a local PC.");
        links.Link(original.Id, asset.AssetId); otherLinks.Link(otherLocal.Id, asset.AssetId);
        check(links.GetLink(original.Id)!.AssetId == otherLinks.GetLink(otherLocal.Id)!.AssetId && original.Id != otherLocal.Id,
            "Two administrator databases can link different local computer IDs to one global asset ID.");
        check(runtime.FindComputer(original.Id) is { Room: "201Д", Seat: "001", LocalPlacement: true },
            "Explicit link projects shared placement and protects it from GLPI location updates.");

        var stale = runtime.FindComputer(original.Id)!;
        var moved = asset with { RoomDisplay = "415Д", Seat = "007", Uuid = Guid.NewGuid().ToString(), Serial = "SERVER-ONLY" };
        var notifications = 0; links.Changed += () => notifications++;
        links.ApplySnapshot("nas-main", [moved, secondAsset]);
        check(notifications is > 0 and < 4, "Projection updates finish without recursive runtime feedback.");
        check(runtime.FindComputer(original.Id) is { Room: "415Д", Seat: "007" }, "Another administrator's move updates the local room projection.");
        check(runtime.FindComputer(original.Id)!.Uuid == original.Uuid && runtime.FindComputer(original.Id)!.Serial == original.Serial,
            "Server inventory identifiers never replace locally trusted operational hardware identifiers.");
        stale.Name = "lab-shared-renamed";
        links.SaveComputer(stale);
        check(runtime.FindComputer(original.Id) is { Room: "415Д", Seat: "007", Name: "lab-shared-renamed" },
            "Saving a stale computer draft preserves the latest shared placement while saving local settings.");
        var incoming = original.Copy(); incoming.Room = "Данные GLPI"; incoming.Name = "lab-shared";
        runtime.Import([incoming]);
        check(runtime.Computers.Count == 1 && runtime.FindComputer(original.Id)!.Room == "415Д" && links.GetLink(original.Id) is not null,
            "Repeated hardware import preserves local identity, shared link and shared room placement.");

        var duplicate = original.Copy(); duplicate.Id = Guid.NewGuid().ToString("N"); duplicate.Name = "lab-shared-astra";
        runtime.SaveComputer(duplicate);
        check(Rejects(() => links.Link(duplicate.Id, asset.AssetId)), "One shared asset cannot be linked to two cards in one administrator database.");
        links.Link(duplicate.Id, secondAsset.AssetId);
        var schedule = new LabSchedule { Name = "Future fixture", ComputerIds = [original.Id, duplicate.Id], NextRun = DateTimeOffset.Now.AddDays(1) };
        runtime.SaveSchedule(schedule);
        check(Rejects(() => links.MergeComputers(original.Id, duplicate.Id)) && runtime.Computers.Count == 2 && links.Links.Count == 2,
            "Merging cards bound to different inventory assets fails without removing either card or link.");
        links.Unlink(original.Id);
        check(runtime.FindComputer(original.Id)!.Room == "415Д", "Unlinking preserves the last known local placement.");
        links.MergeComputers(original.Id, duplicate.Id);
        check(runtime.Computers.Count == 1 && links.GetLink(original.Id)?.AssetId == secondAsset.AssetId && links.GetLink(duplicate.Id) is null,
            "Local merge transfers the secondary card's inventory link to the surviving local ID.");
        check(runtime.Schedules.Single().ComputerIds.SequenceEqual([original.Id]) && runtime.FindComputer(original.Id)!.Room == "301Д",
            "Local merge redirects schedules and reapplies the surviving inventory placement.");
        var persistedLink = store.Load<InventoryComputerLink>(InventoryLinkService.RecordKind).Single();
        check(persistedLink.LocalComputerId == original.Id && store.Load<LabComputer>("computer").Count == 1 &&
            store.Load<LabSchedule>("schedule").Single().ComputerIds.SequenceEqual([original.Id]),
            "Merged computers, links and schedule references persist together.");

        var beforeRollback = runtime.FindComputer(original.Id)!;
        var rejectedTransaction = false;
        try { store.ReplaceComputerState([], [], [persistedLink, persistedLink]); }
        catch (Microsoft.Data.Sqlite.SqliteException) { rejectedTransaction = true; }
        check(rejectedTransaction && store.Load<LabComputer>("computer").Single().Id == beforeRollback.Id &&
            store.Load<LabSchedule>("schedule").Count == 1 && store.Load<InventoryComputerLink>(InventoryLinkService.RecordKind).Count == 1,
            "A failed multi-record merge transaction rolls back computers, schedules and inventory links.");

        links.ApplySnapshot("nas-main", [secondAsset with { Archived = true }]);
        check(links.GetStatus(original.Id).Contains("архивирован") && runtime.FindComputer(original.Id) is not null,
            "Archived inventory assets retain local computer cards and display their archived state.");
        links.ApplySnapshot("nas-main", []);
        check(links.GetStatus(original.Id).Contains("не найден") && runtime.FindComputer(original.Id)!.Room == "301Д",
            "Missing assets retain their last placement and produce an explicit link status.");
        links.ApplySnapshot("different-nas", [secondAsset with { RoomDisplay = "Чужой корпус" }]);
        check(links.GetStatus(original.Id).Contains("другому серверу") && runtime.FindComputer(original.Id)!.Room == "301Д" &&
            !links.CanOpenInventory(original.Id), "A different server instance cannot reuse local bindings or overwrite their placement.");
        links.ClearSnapshot();
        check(links.CurrentInstanceId == "" && !links.CanOpenInventory(original.Id) && links.GetLink(original.Id) is not null &&
            runtime.FindComputer(original.Id)!.Room == "301Д", "Changing a server connection clears active navigation without deleting links or cached placement.");

        links.ApplySnapshot("nas-main", [secondAsset]);
        using (var viewModel = new LabViewModel(store, configuration, new LabSecrets(credentials), runtime, new FakeSessions(), new FakeLauncher()))
        {
            viewModel.AttachInventoryLinks(links);
            viewModel.SelectedComputer = viewModel.Computers.Single();
            check(viewModel.LinkedPlacementReadOnly, "The legacy editor marks linked classroom and seat as read-only.");
            var requested = ""; viewModel.MoveInInventoryRequested += id => requested = id;
            viewModel.MoveInInventoryCommand.Execute(null);
            check(requested == secondAsset.AssetId, "Legacy move action navigates by the global inventory asset ID.");
            viewModel.ComputerDraft.Room = "Старый черновик"; viewModel.ComputerDraft.Seat = "999";
            viewModel.SaveComputerCommand.Execute(null);
            check(runtime.FindComputer(original.Id) is { Room: "301Д", Seat: "002" }, "Legacy save command cannot restore a stale linked room or seat.");
            links.Unlink(original.Id); viewModel.Refresh();
            check(!viewModel.LinkedPlacementReadOnly, "Unlinking restores local placement editing.");
        }
        check(otherRuntime.FindComputer(otherLocal.Id)!.Room == "201Д", "One client's local link changes do not mutate another client's local database.");
        var offlineStore = new LabStore(Path.Combine(directory, "offline.db"));
        var offlineFirst = original.Copy(); offlineFirst.Room = "101Д"; offlineFirst.LocalPlacement = true;
        var offlineSecond = duplicate.Copy(); offlineSecond.Room = "509Д"; offlineSecond.Seat = "025";
        offlineStore.ReplaceComputers([offlineFirst, offlineSecond]);
        offlineStore.Save(InventoryLinkService.RecordKind, offlineSecond.Id, new InventoryComputerLink("nas-main", offlineSecond.Id, "offline-asset"));
        using var offlineRuntime = new LabRuntime(offlineStore, configuration, []);
        using var offlineLinks = new InventoryLinkService(offlineStore, offlineRuntime);
        offlineLinks.MergeComputers(offlineFirst.Id, offlineSecond.Id);
        check(offlineStore.Load<LabComputer>("computer").Single() is { Room: "509Д", Seat: "025" } &&
            offlineLinks.GetLink(offlineFirst.Id)?.AssetId == "offline-asset",
            "Merging before a server snapshot arrives atomically preserves the linked secondary card's cached placement.");
        return Task.CompletedTask;
    }

    private static bool Rejects(Action operation)
    {
        try { operation(); return false; }
        catch (InvalidOperationException) { return true; }
    }
}
