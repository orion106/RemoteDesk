using System.Windows.Controls;

namespace RemoteAssist;

public partial class InventoryAssetEditor : UserControl
{
    public InventoryAssetEditor(ResourceDictionary theme, ResourceDictionary styles)
    {
        Resources.MergedDictionaries.Add(theme); Resources.MergedDictionaries.Add(styles); InitializeComponent();
    }
    private void ClearParent_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InventoryViewModel vm) return;
        vm.ClearDraftParent();
    }
}
