using Microsoft.Win32;
using System.Windows.Controls;

namespace RemoteAssist;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private string _appliedTheme = "Тёмная";
    private bool _waitingForUpdate;
    private bool _allowClose;
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.Apply(this, _appliedTheme);
        DataContextChanged += (_, args) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.Settings.PasswordResetRequested -= ResetPassword;
                _viewModel.Settings.Saved -= ApplyTheme;
                _viewModel.Settings.CategoryChanged -= ScrollSettingsToTop;
            }
            _viewModel = args.NewValue as MainViewModel;
            if (_viewModel is not null)
            {
                _viewModel.Settings.PasswordResetRequested += ResetPassword;
                _viewModel.Settings.Saved += ApplyTheme;
                _viewModel.Settings.CategoryChanged += ScrollSettingsToTop;
                ApplyTheme();
            }
        };
        SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        Closing += async (_, args) =>
        {
            if (_allowClose) return;
            if (_viewModel?.LoadUpdate.IsBusy != true && _viewModel?.Lab is null) return;
            args.Cancel = true;
            if (_waitingForUpdate) return;
            _waitingForUpdate = true;
            if (_viewModel?.Lab is not null) await _viewModel.Lab.StopAsync();
            if (_viewModel is not null) await _viewModel.LoadUpdate.CancelAndWaitAsync();
            _ = Dispatcher.BeginInvoke(() => { _allowClose = true; Close(); });
        };
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
            if (_viewModel is not null)
            {
                _viewModel.Settings.PasswordResetRequested -= ResetPassword;
                _viewModel.Settings.Saved -= ApplyTheme;
                _viewModel.Settings.CategoryChanged -= ScrollSettingsToTop;
                _viewModel.Dispose();
            }
        };
    }
    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is SettingsViewModel settings)
        {
            using var password = box.SecurePassword;
            settings.SetPassword(password);
        }
    }
    private void ResetPassword() => AdPassword.Clear();
    private void ScrollSettingsToTop() => SettingsScroll.ScrollToTop();
    private void ApplyTheme()
    {
        _appliedTheme = _viewModel?.Settings.Draft.Theme ?? "Тёмная";
        ThemeManager.Apply(this, _appliedTheme);
    }
    private void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_appliedTheme == "System" && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(() => ThemeManager.Apply(this, _appliedTheme));
    }
}
