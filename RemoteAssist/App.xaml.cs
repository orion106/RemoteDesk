namespace RemoteAssist;

public partial class App : Application
{
    private DirectoryConfiguration? _configuration;
    private MainViewModel? _viewModel;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _configuration = new DirectoryConfiguration(UserSettings.Load(), new WindowsCredentialStore());
        var sessions = new SessionService(); var rdp = new RemoteDesktopLauncher();
        var store = new LabStore(LabStore.DefaultPath); var secrets = new LabSecrets(new WindowsCredentialStore());
        var astra = new AstraLabAdapter(_configuration, secrets);
        var runtime = new LabRuntime(store, _configuration, [new WindowsLabAdapter(_configuration), astra]);
        var lab = new LabViewModel(store, _configuration, secrets, runtime, sessions, rdp, astra);
        var inventoryLinks = new InventoryLinkService(store, runtime);
        lab.AttachInventoryLinks(inventoryLinks);
        var inventory = new InventoryViewModel(new InventoryClient(store, new WindowsCredentialStore()), runtime, inventoryLinks);
        _viewModel = new MainViewModel(new ActiveDirectoryService(_configuration), sessions, rdp, _configuration, lab: lab, inventory: inventory);
        lab.MoveInInventoryRequested += id => { inventory.SelectAsset(id); inventory.Tab = 1; _viewModel.Section = "Инвентаризация"; };
        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();
        lab.Start();
        inventory.Start();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _configuration?.Dispose();
        base.OnExit(e);
    }
}
