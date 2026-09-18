using System.Windows.Controls;

namespace RemoteAssist;

public partial class LabView : UserControl
{
    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(nameof(Section), typeof(string), typeof(LabView), new PropertyMetadata("Аудитории"));
    public string Section { get => (string)GetValue(SectionProperty); set => SetValue(SectionProperty, value); }
    private LabViewModel? _vm;
    public LabView()
    {
        InitializeComponent();
        DataContextChanged += (_, args) => { if (_vm is not null) _vm.SecretsReset -= ResetSecrets; _vm = args.NewValue as LabViewModel; if (_vm is not null) _vm.SecretsReset += ResetSecrets; };
    }
    private void SecretChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && _vm is not null && box.Tag is string kind) { using var value = box.SecurePassword; _vm.SetSecret(kind, value); }
    }
    private void ResetSecrets() { GlpiUserSecret.Clear(); GlpiAppSecret.Clear(); SshSecret.Clear(); }
}
