using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteAssist;
using RemoteAssist.Inventory;

namespace RemoteAssist.Tests;

internal static class Program
{
    private static int _assertions;
    private static readonly string ArtifactDirectory = Path.GetFullPath("artifacts");
    private static readonly string TestDirectory = Path.Combine(ArtifactDirectory, "tests-" + Guid.NewGuid().ToString("N"));
    private static readonly BindingListener Bindings = new();

    [STAThread]
    private static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources["InverseBooleanConverter"] = new InverseBooleanConverter();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        PresentationTraceSources.DataBindingSource.Listeners.Add(Bindings);
        var exit = 0;
        application.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                Directory.CreateDirectory(TestDirectory);
                TestMasksAndCredentials();
                TestSettingsAndStorage();
                TestWindowsCredentialStore();
                await TestSearch();
                await TestSettingsChangeDuringSearch();
                await LoadUpdateTests.Run(TestDirectory, Check);
                await LabTests.Run(TestDirectory, Check);
                await InventoryLinkTests.Run(TestDirectory, Check);
                await InventoryClientTests.Run(TestDirectory, Check);
                await RenderAndCheckInterface();
                Check(Bindings.Errors.Count == 0, "No WPF binding errors: " + string.Join(" | ", Bindings.Errors));
                Console.WriteLine($"PASS: {_assertions} assertions. UI images: {Path.Combine(ArtifactDirectory, "ui")}");
            }
            catch (Exception error) { Console.Error.WriteLine(error); exit = 1; }
            finally { application.Shutdown(); }
        });
        application.Run();
        return exit;
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static SecureString Secret(string value)
    {
        var secure = new SecureString();
        foreach (var c in value) secure.AppendChar(c);
        secure.MakeReadOnly();
        return secure;
    }
    private static string Reveal(SecureString? value) => value is null ? "" : new NetworkCredential("", value).Password;
    private static void TestMasksAndCredentials()
    {
        var masks = DirectoryFilters.ParseMasks("dc*-*\r\nnote-dc*-*\nDC*-*");
        Check(masks.Length == 2, "Masks are deduplicated without case.");
        foreach (var name in new[] { "DC01-WS01", "NOTE-DC2-15", "dc7-test", "NoTe-dc8-6" })
            Check(DirectoryFilters.AllowsComputer(name, masks), "Allowed computer: " + name);
        foreach (var name in new[] { "WS-15", "AD01", "xdc1-2", "DC01" })
            Check(!DirectoryFilters.AllowsComputer(name, masks), "Denied computer: " + name);
        // LDAP matches the name attribute, not a DNS suffix; the caller supplies the short name.
        var filter = DirectoryFilters.Computers("abc*)(name=*)", masks);
        Check(filter.Contains("(|(name=dc*-*)(name=note-dc*-*))"), "OR masks are present.");
        Check(filter.Contains(@"abc\2a\29\28name=\2a\29"), "Search input is escaped.");
        Check(DirectoryFilters.Computers("", ["dc*)(name=*)"]).Contains(@"(name=dc*\29\28name=*\29)"), "Mask punctuation is escaped, only star is wildcard.");
        Check(DirectoryFilters.AllowsComputer("dc(1)-2", ["dc(1)-*"]), "Mask punctuation is literal.");
        Check(!DirectoryFilters.AllowsComputer("dc1-2", ["dc(1)-*"]), "Regex punctuation is not executable.");
        var rejected = false;
        try { DirectoryFilters.ParseMasks("\r\n ; "); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Empty masks rejected.");
        using var password = Secret("Synthetic-only-password");
        var domain = DirectoryFilters.Credentials(@"CONTOSO\admin", password);
        Check(domain.Domain == "CONTOSO" && domain.UserName == "admin", "DOMAIN login parsed.");
        var upn = DirectoryFilters.Credentials("admin@contoso.local", password);
        Check(upn.UserName == "admin@contoso.local" && upn.Domain == "", "UPN parsed.");
        Check(DirectoryErrors.Describe(new System.DirectoryServices.Protocols.LdapException(49)).Contains("отклонил"), "Invalid credentials are readable.");
    }

    private static void TestSettingsAndStorage()
    {
        var path = Path.Combine(TestDirectory, "settings.json");
        using var store = new MemoryStore();
        using var config = new DirectoryConfiguration(new UserSettings(), store, path);
        using var vm = new SettingsViewModel(config, new FakeDirectory());
        vm.Draft.DomainController = "not-applied";
        vm.MasksText = "invalid";
        vm.CancelCommand.Execute(null);
        Check(config.Settings.DomainController is null && vm.MasksText.Contains("dc*-*"), "Cancel restores the draft.");
        vm.Draft.UseExplicitCredentials = true;
        vm.Draft.AdUserName = @"TEST\admin";
        vm.Draft.DomainController = "ad.example.test";
        using var secret = Secret("Synthetic-secret-123!");
        vm.SetPassword(secret);
        vm.SaveCommand.Execute(null);
        Check(config.Settings.UseExplicitCredentials, "Explicit account saved.");
        var json = File.ReadAllText(path);
        Check(!json.Contains("Synthetic-secret") && !json.Contains("SecureString"), "No password in JSON.");
        using (var restored = new DirectoryConfiguration(UserSettings.Load(path), store, path))
        using (var recovered = restored.GetPassword(restored.Settings))
            Check(Reveal(recovered) == "Synthetic-secret-123!", "Password restored from the credential store.");
        vm.DeletePasswordCommand.Execute(null);
        using (var exists = store.Read(WindowsCredentialStore.Target(config.Settings)))
            Check(exists is not null, "Deleting password is deferred until Save.");
        vm.CancelCommand.Execute(null);
        using (var exists = config.GetPassword(config.Settings)) Check(exists is not null, "Cancel keeps password.");
        vm.DeletePasswordCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        using (var removed = config.GetPassword(config.Settings)) Check(removed is null, "Password removed from memory and storage.");
        Check(!config.Settings.RememberPassword, "Deletion disables persistence.");
        vm.MasksText = "";
        vm.SaveCommand.Execute(null);
        Check(vm.Message.Contains("маску") && config.Settings.ComputerMasks.Length == 2, "Invalid masks do not change saved settings.");
        Check(UserSettings.Load(path).AdUserName == @"TEST\admin", "Saved settings reload.");
        var oldPath = Path.Combine(TestDirectory, "legacy.json");
        File.WriteAllText(oldPath, """{"Theme":"System","DomainController":"dc.example"}""");
        var legacy = UserSettings.Load(oldPath);
        Check(legacy.Theme == "System" && legacy.ComputerMasks.SequenceEqual(new[] { "dc*-*", "note-dc*-*" }), "Legacy settings migrate without changing the selected theme.");
        Check(WindowsCredentialStore.Target(new UserSettings { AdUserName = @"A\b", DomainController = "dc1" }) !=
              WindowsCredentialStore.Target(new UserSettings { AdUserName = @"A\b", DomainController = "dc2" }), "Credential entries are controller-specific.");
        var remembered = new UserSettings { UseExplicitCredentials = true, AdUserName = @"SESSION\admin", RememberPassword = true };
        store.Write(WindowsCredentialStore.Target(remembered), remembered.AdUserName, secret);
        using var sessionOnly = new DirectoryConfiguration(remembered, store, Path.Combine(TestDirectory, "session-only.json"));
        var sessionDraft = remembered.Copy();
        sessionDraft.RememberPassword = false;
        sessionOnly.Save(sessionDraft, null, false);
        using (var sessionPassword = sessionOnly.GetPassword(sessionOnly.Settings)) Check(sessionPassword is not null, "Turning off remember retains the password for this run.");
        using (var deleted = store.Read(WindowsCredentialStore.Target(remembered))) Check(deleted is null, "Turning off remember deletes persisted credential.");
        using var restarted = new DirectoryConfiguration(sessionDraft, store, Path.Combine(TestDirectory, "session-only.json"));
        using (var forgotten = restarted.GetPassword(restarted.Settings)) Check(forgotten is null, "Session-only password does not survive restart.");
    }

    private static void TestWindowsCredentialStore()
    {
        var store = new WindowsCredentialStore();
        var target = "RemoteAssist.Tests/" + Guid.NewGuid().ToString("N");
        using var secret = Secret("Тестовый-пароль-only-123!");
        try
        {
            store.Write(target, @"TEST\synthetic", secret);
            using (var read = store.Read(target)) Check(Reveal(read) == "Тестовый-пароль-only-123!", "Native Windows credentials roundtrip.");
            store.Delete(target);
            using (var removed = store.Read(target)) Check(removed is null, "Native credential deletion.");
        }
        finally { store.Delete(target); }
    }

    private static async Task TestSearch()
    {
        using var store = new MemoryStore();
        using var config = new DirectoryConfiguration(new UserSettings(), store, Path.Combine(TestDirectory, "search.json"));
        var sessions = new FakeSessions();
        var directory = new FakeDirectory { IncludeUnmatchedComputer = true };
        using var vm = new MainViewModel(directory, sessions, new FakeLauncher(), config);
        vm.SearchByComputer = false;
        vm.Query = "Иван";
        await vm.SearchAsync(false);
        Check(sessions.Requested.All(n => !n.StartsWith("OTHER")), "Nonmatching computer is not polled when searching for users.");
        Check(vm.Errors == 1 && vm.Completed == 3, "Error is counted before filtering sessions.");
        Check(vm.Results.Any(r => r.Computer == "DC02-OFFLINE"), "Unavailable computer remains visible.");
        Check(vm.Results.Any(r => r.DisplayName == "Иван Петров" && r.Session.Id == -1 && r.Status.Contains("не все")), "Missing user's result flags partial search.");
        Check(vm.ActiveSessions == 1, "Analytics counts active sessions.");
        Check(vm.ScanState.Contains("неполный"), "Analytics flags incomplete scan.");
        directory.Count = 100;
        directory.IncludeUnmatchedComputer = false;
        vm.SearchByComputer = true;
        await vm.SearchAsync(true);
        Check(vm.Completed == 100 && sessions.MaxConcurrent <= 8 && sessions.MaxConcurrent > 1, "100 computers scanned with bounded concurrency.");
    }

    private static async Task TestSettingsChangeDuringSearch()
    {
        using var store = new MemoryStore();
        using var config = new DirectoryConfiguration(new UserSettings(), store, Path.Combine(TestDirectory, "change.json"));
        using var vm = new MainViewModel(new FakeDirectory { Count = 40 }, new FakeSessions { Delay = 100 }, new FakeLauncher(), config);
        var work = vm.SearchAsync(false);
        await Task.Delay(20);
        var changed = config.Settings.Copy();
        changed.ComputerMasks = ["note-dc*-*"];
        config.Save(changed, null, false);
        await work;
        Check(vm.Results.Count == 0 && vm.Total == 0 && !vm.IsBusy, "Cancelled old scan cannot repopulate results after settings change.");
    }

    private static async Task RenderAndCheckInterface()
    {
        using var store = new MemoryStore();
        using var config = new DirectoryConfiguration(new UserSettings(), store, Path.Combine(TestDirectory, "ui.json"));
        var labStore = new LabStore(Path.Combine(TestDirectory,"ui-lab.db"));
        using var runtime = new LabRuntime(labStore,config,[new LabTests.FakeAdapter()],(_,_,_)=>Task.FromResult(true));
        runtime.Computers.AddRange(Enumerable.Range(1,8).Select(i=> { var pc=LabTests.Computer("CLASS-301-"+i.ToString("00"));pc.Room="301";pc.Seat=i.ToString();pc.Position=i;return pc; }));
        using var lab = new LabViewModel(labStore,config,new LabSecrets(store),runtime,new FakeSessions(),new FakeLauncher());
        var inventoryClient = new InventoryClient(labStore, store, Path.Combine(TestDirectory, "photo-cache"));
        inventoryClient.Configure("https://inventory.example.test/", "test");
        inventoryClient.Snapshot.InstanceId = "ui-test";
        inventoryClient.Snapshot.RetrievedUtc = DateTimeOffset.Now;
        foreach (var floor in CorpusMaps.Floors)
        {
            Check(floor.Rooms.Count >= 19, "All floor geometries loaded: " + floor.Floor);
            Check(floor.Rooms.All(x => !x.Geometry.Bounds.IsEmpty), "Room geometry is nonempty: " + floor.Floor);
            Check(!floor.Corridor.Bounds.IsEmpty && floor.Corridor.GetArea() > 0, "Corridor geometry exists: " + floor.Floor);
            Check(floor.Rooms.All(x => !floor.Corridor.FillContains(new Point(x.Geometry.Bounds.X + x.Geometry.Bounds.Width / 2, x.Geometry.Bounds.Y + x.Geometry.Bounds.Height / 2))), "Corridors exclude room interiors: " + floor.Floor);
            inventoryClient.Snapshot.Rooms.AddRange(floor.Rooms.Select(r => new RoomDto { Number = r.Number, Floor = floor.Floor, Version = 1 }));
        }
        var room301 = inventoryClient.Snapshot.Rooms.First(x => x.Number == "301");
        room301.Name = "Компьютерный класс";
        inventoryClient.Snapshot.Assets.AddRange(Enumerable.Range(1, 6).Select(i => new AssetDto { Version = 1, Type = i == 6 ? "Принтер" : "Системный блок", Model = i == 6 ? "HP LaserJet" : "Учебный компьютер", Hostname = "CLASS-301-" + i.ToString("00"), InventoryNumber = "00012" + i, RoomId = room301.Id, Seat = i.ToString(), X = i <= 4 ? (i - 1) % 2 * .5 + .1 : null, Y = i <= 4 ? (i - 1) / 2 * .5 + .1 : null }));
        inventoryClient.Snapshot.Cartridges.Add(new CartridgeDto { Version = 1, Number = "К-001", Model = "CF283A", Status = "Установлен", PrinterId = inventoryClient.Snapshot.Assets.Last().Id, Slot = "Чёрный" });
        inventoryClient.Snapshot.Audits.Add(new AuditDto { Version = 1, Name = "Проверка класса 301", Items = inventoryClient.Snapshot.Assets.Select(a => new AuditItemDto { Version = 1, AssetId = a.Id, AssetSnapshot = InventoryViewModel.Copy(a), ExpectedRoomId = room301.Id, ExpectedRoomNumber = "301Д" }).ToList() });
        inventoryClient.Snapshot.Assets[1].Type = "Монитор";
        inventoryClient.Snapshot.Assets[1].ParentAssetId = inventoryClient.Snapshot.Assets[0].Id;
        foreach (var state in new[] { "Запас", "Списан", "На заправке" }) inventoryClient.Snapshot.Cartridges.Add(new CartridgeDto { Version = 1, Number = "К-" + state, Model = "CF283A", Status = state });
        var corridor3 = new RoomDto { Version = 1, Number = "Коридор 3", Floor = 3, Kind = "corridor" }; inventoryClient.Snapshot.Rooms.Add(corridor3);
        var corridorMap = CorpusMaps.Floors[2]; var corridorBounds = corridorMap.Drawing.Bounds;
        var corridorPoint = (from x in Enumerable.Range(1, 99) from y in Enumerable.Range(1, 99) let p = new Point(x / 100.0, y / 100.0) where corridorMap.Corridor.FillContains(new Point(corridorBounds.X + p.X * corridorBounds.Width, corridorBounds.Y + p.Y * corridorBounds.Height)) select p).First();
        inventoryClient.Snapshot.Assets.Add(new AssetDto { Version = 1, Type = "Телефон", Model = "Телефон охраны", RoomId = corridor3.Id, PhoneNumber = "001-23 доб. 05", X = corridorPoint.X, Y = corridorPoint.Y });
        var fixturePhoto = new PhotoDto { Version = 1, OwnerId = inventoryClient.Snapshot.Assets.First().Id, Caption = "Тестовое изображение — монитор", IsPrimary = true, ContentType = "image/png" };
        inventoryClient.Snapshot.Photos.Add(fixturePhoto);
        var photoDirectory = Path.Combine(inventoryClient.PhotoCacheDirectory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inventoryClient.Connection.Url))));
        Directory.CreateDirectory(photoDirectory);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) { drawing.DrawRectangle(Brushes.LightSlateGray, null, new Rect(0, 0, 320, 200)); drawing.DrawRoundedRectangle(Brushes.DimGray, null, new Rect(45, 22, 230, 138), 8, 8); drawing.DrawRectangle(Brushes.SkyBlue, null, new Rect(55, 32, 210, 112)); drawing.DrawRectangle(Brushes.DimGray, null, new Rect(146, 160, 28, 23)); drawing.DrawRectangle(Brushes.DimGray, null, new Rect(110, 183, 100, 7)); }
        var fixtureImage = new RenderTargetBitmap(320, 200, 96, 96, PixelFormats.Pbgra32); fixtureImage.Render(visual);
        var fixtureEncoder = new PngBitmapEncoder(); fixtureEncoder.Frames.Add(BitmapFrame.Create(fixtureImage));
        using (var photoFile = File.Create(Path.Combine(photoDirectory, fixturePhoto.Id + ".image"))) fixtureEncoder.Save(photoFile);
        using var inventory = new InventoryViewModel(inventoryClient);
        using var vm = new MainViewModel(new FakeDirectory(), new FakeSessions(), new FakeLauncher(), config, new TestUpdateSessionFactory(), lab, inventory);
        var window = new MainWindow
        {
            DataContext = vm, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            ShowInTaskbar = false, ShowActivated = false, Width = 1240, Height = 820
        };
        window.Show();
        try
        {
            await Pump();
            Check(window.FindName("SearchBox") is TextBox { ActualWidth: > 300 }, "Search layout expands.");
            var search = (TextBox)window.FindName("SearchBox");
            var placeholder = (TextBlock)search.Template.FindName("Placeholder", search);
            Check(placeholder.Visibility == Visibility.Visible, "Placeholder initially visible.");
            vm.Query = "DC01";
            await Pump();
            Check(placeholder.Visibility == Visibility.Collapsed, "Placeholder hides when text is entered.");
            await vm.SearchAsync(false);
            // Test data is injected only in this test process.
            vm.Results.Insert(0, new SessionRow(new UserSession("NOTE-DC2-15", "CONTOSO", "i.ivanov", 2, "Active", DateTime.Now, null), "Иван Иванов"));
            await Render(window, "home-dark", 1);
            vm.Section = "Настройки";
            vm.Settings.Draft.UseExplicitCredentials = true;
            vm.Settings.Draft.AdUserName = @"CONTOSO\support";
            await Pump();
            var password = (PasswordBox)window.FindName("AdPassword");
            Check(password.IsEnabled, "Explicit account enables password editor.");
            await Render(window, "settings-dark", 1);
            var scroll = (ScrollViewer)window.FindName("SettingsScroll");
            scroll.ScrollToEnd();
            await Render(window, "settings-bottom-dark", 1);
            vm.Settings.CancelCommand.Execute(null);
            await Pump();
            Check(!password.IsEnabled, "Cancel restores Windows-account mode.");
            vm.OpenUpdateSettingsCommand.Execute(null);
            await Pump();
            Check(vm.Section == "Настройки" && vm.Settings.Category == "Обновление нагрузки", "Update settings shortcut opens the central settings category.");
            var updateMasks = (TextBox)window.FindName("LoadUpdateMasksBox");
            updateMasks.Text = "DC*-*\nNOTE-DC*-*";
            await Pump();
            Check(updateMasks.DataContext == vm.Settings && vm.Settings.LoadUpdateMasksText.Contains("NOTE-DC*-*"), "Update masks are edited only through the central settings draft.");
            vm.Settings.SaveCommand.Execute(null);
            foreach (var (category, panel, capture) in new[] { ("Рабочие столы", "DesktopSettings", "desktops"), ("Обновление нагрузки", "LoadUpdateSettings", "updates"), ("Оформление", "AppearanceSettings", "appearance") })
            {
                vm.Settings.NavigateCategoryCommand.Execute(category);
                await Pump();
                Check(((StackPanel)window.FindName(panel)).IsVisible && !((StackPanel)window.FindName("ConnectionSettings")).IsVisible, "Only the selected settings category is visible: " + category);
                await Render(window, "settings-" + capture + "-dark", 1);
            }
            vm.Section = "Обновление нагрузки";
            await vm.LoadUpdate.ScanAsync();
            Check(vm.LoadUpdate.Results.Count == 3, "Update tab binds scanned installation rows.");
            await Render(window, "updates-dark", 1);
            var updateScroll = (ScrollViewer)window.FindName("LoadUpdateScroll");
            updateScroll.ScrollToEnd();
            await Render(window, "updates-results-dark", 1);
            var updateTable = (DataGrid)window.FindName("LoadUpdatesTable");
            var updateTableScroll = Descendants<ScrollViewer>(updateTable).First();
            Check(updateTableScroll.ScrollableWidth > 0, "Update table provides horizontal scrolling for full paths and results.");
            var selectionBox = Descendants<CheckBox>(updateTable).First();
            selectionBox.IsChecked = false;
            await Pump();
            Check(!vm.LoadUpdate.Results[0].Selected, "Update checkbox changes the view model selection.");
            vm.LoadUpdate.Results[0].Selected = true;
            vm.Section = "Аналитика";
            await Render(window, "analytics-dark", 1);
            vm.Section = "Главная";
            await vm.SearchAsync(false);
            vm.SidebarExpanded = false;
            window.Width = 780; window.Height = 640;
            await Render(window, "home-compact-200", 2);
            var table = (DataGrid)window.FindName("SessionsTable");
            Check(table.Columns.Last().ActualWidth >= 150, "Connect buttons retain their width in compact mode.");
            var tableScroll = Descendants<ScrollViewer>(table).First();
            Check(tableScroll.ScrollableWidth > 0, "Compact table has horizontal scrolling.");
            ((ScrollViewer)window.FindName("HomeScroll")).ScrollToEnd();
            tableScroll.ScrollToRightEnd();
            await Render(window, "home-compact-scrolled-200", 2);
            ThemeManager.Apply(window, "Светлая");
            await Render(window, "home-light-150", 1.5);
            vm.Section = "Настройки";
            vm.Settings.Category = "Подключение";
            scroll.ScrollToTop();
            await Render(window, "settings-light-200", 2);
            vm.Settings.Category = "Обновление нагрузки";
            await Render(window, "settings-updates-light-200", 2);
            var saveSettings = (Button)window.FindName("SaveSettingsButton");
            var savePosition = saveSettings.TransformToAncestor(window).Transform(new Point(0, 0));
            Check(saveSettings.IsVisible && savePosition.Y + saveSettings.ActualHeight < window.ActualHeight, "Save remains visible in a compact settings window.");
            vm.Section = "Обновление нагрузки";
            updateScroll.ScrollToTop();
            await Render(window, "updates-compact-light-200", 2);
            updateScroll.ScrollToEnd();
            updateTableScroll.ScrollToRightEnd();
            await Render(window, "updates-results-compact-light-200", 2);
            Check(((FrameworkElement)window.Content).ActualWidth > 700, "Compact layout remains measurable.");
            window.Width=1240;window.Height=900;vm.SidebarExpanded=true;ThemeManager.Apply(window,"Тёмная");
            foreach(var section in new[]{"Аудитории","Программы","Очистка профилей","Мониторинг","Задания"})
            {
                vm.Section=section;await Render(window,"lab-"+section+"-dark",1);
                Check(Descendants<LabView>(window).Single().IsVisible,"Lab view is visible: "+section);
            }
            vm.Section="Аудитории";window.Width=780;window.Height=640;vm.SidebarExpanded=false;ThemeManager.Apply(window,"Светлая");await Render(window,"lab-rooms-compact-light-200",2);
            vm.Section = "Инвентаризация"; window.Width = 1400; window.Height = 1020; vm.SidebarExpanded = false;
            inventory.Floor = 3; inventory.SelectedRoom = room301; inventory.SelectedAsset = inventory.Assets.First();
            inventory.SelectedAudit = inventory.Audits.First();
            Check(inventory.DraftAsset.Id == inventory.SelectedAsset.Id && inventory.RoomAssets.Count == 6 && inventory.UnplacedAssets.Count == 2, "Inventory selection and unplaced list reflect saved data.");
            inventory.DraftAsset.Notes = "Несохранённый черновик"; inventory.Search = "CLASS";
            Check(inventory.DraftAsset.Notes == "Несохранённый черновик", "Rebuilding inventory lists preserves the draft.");
            inventory.Tab = 1; await Pump();
            var changedRoomId = inventory.Rooms.First(x => x.Number == "303").Id;
            inventory.DraftAsset.RoomId = changedRoomId; inventory.DraftAuditItem.ActualRoomId = changedRoomId;
            inventory.Search = ""; await Pump();
            Check(inventory.DraftAsset.RoomId == changedRoomId && inventory.DraftAuditItem.ActualRoomId == changedRoomId, "Selector ItemsSource refresh preserves unsaved expected/actual room choices.");
            inventory.SelectAsset(fixturePhoto.OwnerId); await inventory.LoadPhotosAsync();
            Check(inventory.Photos.Count == 1 && inventory.Photos[0].Image is not null, "Cached photographs render offline.");
            Check(inventory.SelectedPhoto?.Path is not null, "Full photograph is available from the cached image.");
            Check(!inventory.SaveAssetCommand.CanExecute(null), "Offline copy disables shared writes.");
            foreach (var theme in new[] { "Тёмная", "Светлая" })
            {
                ThemeManager.Apply(window, theme);
                for (var tab = 0; tab < 7; tab++)
                {
                    inventory.Tab = tab; await Render(window, $"inventory-{tab}-{(theme == "Тёмная" ? "dark" : "light")}", 1);
                    if (tab is 2 or 3)
                    {
                        var grid = Descendants<DataGrid>(Descendants<InventoryView>(window).Single()).First(x => x.IsVisible);
                        Check(grid.Columns.All(x => x.ActualWidth > 20), "Inventory table columns have a finite visible width: " + tab + " / " + theme);
                        Check(Descendants<TextBlock>(grid).Any(x => x.IsVisible && x.Text == (tab == 2 ? "К-001" : "Учебный компьютер")), "Inventory table renders saved row text: " + tab + " / " + theme);
                    }
                }
            }
            inventory.Tab = 0;
            foreach (var floor in inventory.Floors)
            {
                inventory.Floor = floor;
                var number = CorpusMaps.Floors[floor - 1].Rooms.First().Number; inventory.ChooseMapRoom(number);
                Check(inventory.SelectedRoom?.Number == number && inventory.SelectedRoom.Floor == floor, "Select room from floor " + floor);
                await Render(window, $"inventory-floor-{floor}", 1);
            }
            inventory.Floor = 3; inventory.SelectedRoom = room301;
            var inventoryView = Descendants<InventoryView>(window).Single();
            inventory.CartridgeGroup = "Списанные";
            Check(inventory.FilteredCartridges.Count == 1 && inventory.FilteredCartridges[0].Status == "Списан", "Written-off cartridges have their own group.");
            Check(inventory.SpareCartridges.Count == 1 && inventory.SpareCartridges[0].Status == "Запас", "Replacement choices exclude written-off and installed cartridges.");
            inventory.CartridgeGroup = "Все";
            inventory.SelectAsset(inventoryClient.Snapshot.Assets[1].Id);
            var editor = new InventoryAssetEditor(window.Resources, inventoryView.Resources) { DataContext = inventory, Margin = new Thickness(20) };
            var card = new Window { Content = editor, Width = 510, Height = 760, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
            card.Resources.MergedDictionaries.Add(window.Resources); card.SetResourceReference(Window.BackgroundProperty, "PanelBrush"); card.SetResourceReference(Window.ForegroundProperty, "TextBrush");
            card.Show();
            try
            {
                await Pump();
                Check(inventory.DraftAsset.ParentAssetId == inventoryClient.Snapshot.Assets[0].Id, "Opening the equipment editor preserves its saved relationship.");
                inventory.DraftAsset.PhoneNumber = "001-23"; inventory.DraftAsset.Notes = "Draft preserved"; inventory.Search = "CLASS"; await Pump();
                Check(inventory.DraftAsset.PhoneNumber == "001-23" && inventory.DraftAsset.ParentAssetId == inventoryClient.Snapshot.Assets[0].Id, "Refresh preserves phone and relationship drafts with the editor open.");
                inventory.ClearDraftParent(); await Pump();
                Check(inventory.DraftAsset.ParentAssetId is null && inventory.DraftAsset.RoomId == room301.Id && inventory.DraftAsset.Notes == "Draft preserved", "Removing a draft link preserves other fields.");
                foreach (var theme in new[] { "Тёмная", "Светлая" }) { ThemeManager.Apply(window, theme); await Render(card, "inventory-card-" + theme, 1); }
            }
            finally { card.Close(); }
            var openedCard = false;
            _ = window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var modal = Application.Current.Windows.Cast<Window>().FirstOrDefault(x => x.Title == "Карточка техники");
                openedCard = modal is not null && Descendants<InventoryAssetEditor>(modal).Any(); modal?.Close();
            }), DispatcherPriority.ApplicationIdle);
            inventory.OpenAssetCard(inventoryClient.Snapshot.Assets[1].Id);
            Check(openedCard, "Map-card action opens the actual equipment dialog.");
            inventory.Search = ""; inventory.SelectedRoom = corridor3;
            foreach (var theme in new[] { "Тёмная", "Светлая" }) { ThemeManager.Apply(window, theme); await Render(window, "inventory-corridor-" + theme, 1); }
            Check(inventory.RoomAssets.Count == 1 && inventory.RoomAssets[0].Value.PhoneNumber == "001-23 доб. 05", "Corridor objects appear separately from rooms.");
            inventory.SelectedRoom = room301;
            var zoom = (Slider)inventoryView.FindName("RoomZoom"); zoom.Value = 2;
            await Render(window, "inventory-zoom-200", 1);
            Check(((ScaleTransform)((RoomPlacementControl)inventoryView.FindName("RoomPlan")).LayoutTransform).ScaleX == 2, "Room zoom binding works.");
            window.Width = 780; window.Height = 640; inventory.Tab = 1;
            await Render(window, "inventory-compact-light-200", 2);
        }
        finally { window.Close(); }
    }
    private static async Task Pump()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(30);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task Render(Window window, string name, double scale)
    {
        await Pump();
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale), (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.Combine(ArtifactDirectory, "ui"));
        using var file = File.Create(Path.Combine(ArtifactDirectory, "ui", name + ".png"));
        encoder.Save(file);
    }
}

internal sealed class MemoryStore : ICredentialStore, IDisposable
{
    private readonly Dictionary<string, SecureString> _passwords = new();
    public SecureString? Read(string target) => _passwords.TryGetValue(target, out var value) ? value.Copy() : null;
    public void Write(string target, string userName, SecureString password) { Delete(target); _passwords[target] = password.Copy(); }
    public void Delete(string target) { if (_passwords.Remove(target, out var value)) value.Dispose(); }
    public void Dispose() { foreach (var value in _passwords.Values) value.Dispose(); _passwords.Clear(); }
}
internal sealed class FakeDirectory : IActiveDirectoryService
{
    public int Count { get; set; } = 3;
    public bool IncludeUnmatchedComputer { get; set; }
    public Task<IReadOnlyList<AdComputer>> FindComputersAsync(string query, CancellationToken token) => GetSearchComputersAsync(token);
    public Task<IReadOnlyList<AdComputer>> FindComputersAsync(string query, IReadOnlyList<string> masks, CancellationToken token) => GetSearchComputersAsync(token);
    public Task<IReadOnlyList<AdComputer>> GetSearchComputersAsync(CancellationToken token)
    {
        var items = Enumerable.Range(1, Count).Select(i => new AdComputer(i == 2 ? "DC02-OFFLINE" : $"DC{i:00}-WS01", null, "")).ToList();
        if (IncludeUnmatchedComputer) items.Add(new AdComputer("OTHER-01", null, ""));
        return Task.FromResult<IReadOnlyList<AdComputer>>(items);
    }
    public Task<IReadOnlyList<AdUser>> FindUsersAsync(string query, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<AdUser>>([new("i.ivanov", "i.ivanov@contoso.test", "Иван Иванов"), new("i.petrov", "i.petrov@contoso.test", "Иван Петров")]);
    public Task<string> TestConnectionAsync(UserSettings settings, SecureString? password, CancellationToken token) =>
        Task.FromResult("Соединение с AD установлено. Область поиска доступна.");
}
internal sealed class FakeSessions : ISessionService
{
    private int _concurrent;
    public int Delay { get; set; } = 10;
    public int MaxConcurrent { get; private set; }
    public List<string> Requested { get; } = [];
    public async Task<IReadOnlyList<UserSession>> GetSessionsAsync(string computer, CancellationToken token, bool force = false)
    {
        Requested.Add(computer);
        MaxConcurrent = Math.Max(MaxConcurrent, ++_concurrent);
        try
        {
            await Task.Delay(Delay, token);
            return computer.Contains("OFFLINE")
                ? [new(computer, null, null, -1, "Ошибка", DateTime.Now, "Компьютер не ответил")]
                : computer.StartsWith("DC01")
                    ? [new(computer, "CONTOSO", "i.ivanov", 1, "Active", DateTime.Now, null)]
                    : [new(computer, null, null, -1, "Нет сеансов", DateTime.Now, null)];
        }
        finally { _concurrent--; }
    }
}
internal sealed class FakeLauncher : IRemoteDesktopLauncher { public void Launch(UserSession session) { } }
internal sealed class BindingListener : TraceListener
{
    public List<string> Errors { get; } = [];
    public override void Write(string? message) { if (message?.Contains("Error:") == true) Errors.Add(message); }
    public override void WriteLine(string? message) => Write(message);
}
