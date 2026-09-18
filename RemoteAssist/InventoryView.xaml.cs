using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteAssist;

public partial class InventoryView : UserControl
{
    private InventoryViewModel? _vm;
    public InventoryView()
    {
        InitializeComponent(); FloorPlan.RoomClicked += number => _vm?.ChooseMapRoom(number);
        FloorPlan.CorridorClicked += () => _vm?.OpenCorridorCommand.Execute(null);
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.ViewPhotoRequested -= ShowPhoto;
            if (_vm is not null) _vm.AssetCardRequested -= ShowAssetCard;
            _vm = e.NewValue as InventoryViewModel; RoomPlan.ViewModel = _vm;
            if (_vm is not null) _vm.ViewPhotoRequested += ShowPhoto;
            if (_vm is not null) _vm.AssetCardRequested += ShowAssetCard;
        };
    }
    private void Assets_DoubleClick(object sender, MouseButtonEventArgs e) { if (sender is DataGrid { SelectedItem: InventoryAssetRow row } && e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement((DataGrid)sender, source) is DataGridRow) _vm?.OpenAssetCard(row.Id); }
    private void ShowAssetCard()
    {
        if (_vm is null) return;
        var owner = Window.GetWindow(this);
        var window = new Window { Owner = owner, Title = "Карточка техники", Width = 510, Height = 760, MinWidth = 410, MinHeight = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = owner.FontFamily, FontSize = 14 };
        window.Resources.MergedDictionaries.Add(owner.Resources); window.Resources.MergedDictionaries.Add(Resources);
        window.SetResourceReference(BackgroundProperty, "PanelBrush"); window.SetResourceReference(ForegroundProperty, "TextBrush");
        var host = new ContentControl(); window.Content = host;
        var editor = new InventoryAssetEditor(owner.Resources, Resources) { DataContext = _vm, Margin = new Thickness(20) }; host.Content = editor;
        window.ShowDialog();
    }
    private void LoginPassword_Changed(object sender, RoutedEventArgs e) { if (sender is PasswordBox p) _vm?.SetPassword(p.Password); }
    private void UserPassword_Changed(object sender, RoutedEventArgs e) { if (sender is PasswordBox p) _vm?.SetUserPassword(p.Password); }
    private void ImportTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: InventoryAssetRow asset, DataContext: Inventory.Excel.SpreadsheetPreviewRow row }) { row.TargetAssetId = asset.Id; row.TargetVersion = asset.Value.Version; }
    }
    private async void PlaceSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.DraftAsset.Version == 0 || _vm.DraftAsset.RoomId != _vm.SelectedRoom?.Id) { _vm.Message = "Выберите сохранённую технику этого кабинета."; return; }
        var x = .5; var y = .5;
        if (_vm.SelectedRoom?.Kind == "corridor")
        {
            var map = CorpusMaps.Floors[Math.Clamp(_vm.SelectedRoom.Floor, 1, 5) - 1]; var b = map.Drawing.Bounds;
            var choices = from i in Enumerable.Range(1, 99) from j in Enumerable.Range(1, 99)
                          let px = i / 100.0 let py = j / 100.0
                          where map.Corridor.FillContains(new Point(b.X + px * b.Width, b.Y + py * b.Height))
                          orderby Math.Abs(px - .5) + Math.Abs(py - .5)
                          select new Point(px, py);
            var at = choices.FirstOrDefault(new Point(double.NaN, double.NaN));
            if (!double.IsFinite(at.X)) { _vm.Message = "На плане не найдена доступная зона коридора."; return; }
            x = at.X; y = at.Y;
        }
        await _vm.PlaceAsync(_vm.DraftAsset.Id, x, y);
    }
    private void ShowPhoto(InventoryPhotoRow photo)
    {
        if (photo.Path is null) { if (_vm is not null) _vm.Message = "Изображение не загружено в локальную копию."; return; }
        try
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(photo.Path); image.EndInit(); image.Freeze();
            new Window { Owner = Window.GetWindow(this), Title = photo.Value.Caption.Length > 0 ? photo.Value.Caption : "Фотография", Width = 950, Height = 700, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.DimGray, Content = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(12) } }.ShowDialog();
        }
        catch (Exception e) { if (_vm is not null) _vm.Message = "Не удалось открыть фотографию: " + e.Message; }
    }
}

public sealed class RoomPlacementControl : FrameworkElement
{
    private InventoryViewModel? _vm;
    private string? _dragId;
    private Point _drag;
    private bool _moved;
    private Point _down;
    private readonly Dictionary<string, Rect> _hitAreas = [];
    public InventoryViewModel? ViewModel
    {
        get => _vm;
        set { if (_vm is not null) _vm.LayoutChanged -= Refresh; _vm = value; if (_vm is not null) _vm.LayoutChanged += Refresh; InvalidateVisual(); }
    }
    public RoomPlacementControl()
    {
        Cursor = Cursors.Hand; ClipToBounds = true; Focusable = true;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) _vm?.CancelLink(); };
        MouseLeftButtonDown += OnDown; MouseMove += OnMove; MouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, _) => { _dragId = null; InvalidateVisual(); };
    }
    private void Refresh() => InvalidateVisual();
    private Brush Color(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight); dc.DrawRoundedRectangle(Color("InputBrush", Brushes.WhiteSmoke), new Pen(Color("LineBrush", Brushes.Gray), 1), bounds, 8, 8);
        var grid = new Pen(Color("LineBrush", Brushes.LightGray), .4);
        for (var x = 20; x < ActualWidth; x += 30) dc.DrawLine(grid, new Point(x, 12), new Point(x, ActualHeight - 12));
        for (var y = 20; y < ActualHeight; y += 30) dc.DrawLine(grid, new Point(12, y), new Point(ActualWidth - 12, y));
        _hitAreas.Clear(); if (_vm is null) return;
        var corridor = _vm.SelectedRoom?.Kind == "corridor";
        if (corridor)
        {
            dc.DrawRectangle(Brushes.White, null, bounds);
            dc.PushTransform(CorridorTransform()); dc.DrawDrawing(CorridorMap.Drawing);
            dc.DrawGeometry(new SolidColorBrush(System.Windows.Media.Color.FromArgb(65, 0, 122, 204)), null, CorridorMap.Corridor); dc.Pop();
        }
        foreach (var row in _vm.RoomAssets.Where(x => x.Value.X.HasValue && x.Value.Y.HasValue))
        {
            var p = Position(row.Value); _hitAreas[row.Id] = new Rect(p.X, p.Y, 116, 52);
        }
        foreach (var row in _vm.RoomAssets)
            if (row.Value.ParentAssetId is string parent && _hitAreas.TryGetValue(row.Id, out var childBox) && _hitAreas.TryGetValue(parent, out var parentBox))
                dc.DrawLine(new Pen(Color("AccentBrush", Brushes.DodgerBlue), 2) { DashStyle = DashStyles.Dash }, new Point(childBox.X + 58, childBox.Y + 26), new Point(parentBox.X + 58, parentBox.Y + 26));
        foreach (var row in _vm.RoomAssets.Where(x => x.Value.X.HasValue && x.Value.Y.HasValue))
        {
            var asset = row.Value;
            var p = Position(asset);
            var rect = new Rect(p.X, p.Y, 116, 52); _hitAreas[row.Id] = rect;
            var selected = row.Id == _vm.DraftAsset.Id;
            dc.DrawRoundedRectangle(Color(selected ? "AccentSoftBrush" : "PanelBrush", Brushes.White), new Pen(Color(selected ? "AccentBrush" : "LineBrush", Brushes.SteelBlue), selected ? 2 : 1), rect, 6, 6);
            var symbol = asset.Type.Contains("Монитор", StringComparison.OrdinalIgnoreCase) ? "▣" : asset.Type.Contains("принтер", StringComparison.OrdinalIgnoreCase) || asset.Type.Contains("МФУ", StringComparison.OrdinalIgnoreCase) ? "▤" : "▰";
            Text(dc, symbol + " " + (asset.Seat.Length > 0 ? "Место " + asset.Seat : asset.Type), new Point(p.X + 7, p.Y + 6), 11, 102);
            Text(dc, asset.PhoneNumber.Length > 0 ? "☎ " + asset.PhoneNumber : asset.Hostname.Length > 0 ? asset.Hostname : asset.Model.Length > 0 ? asset.Model : asset.InventoryNumber, new Point(p.X + 7, p.Y + 28), 10, 102);
        }
        if (_hitAreas.Count == 0) Text(dc, "Добавьте технику из списка «Не расставлено»", new Point(26, 28), 14, Math.Max(120, ActualWidth - 55));
    }
    private FloorMap CorridorMap => CorpusMaps.Floors[Math.Clamp(_vm?.SelectedRoom?.Floor ?? 1, 1, 5) - 1];
    private MatrixTransform CorridorTransform()
    {
        // Leave enough space for the whole device card even at the edge of the plan.
        var b = CorridorMap.Drawing.Bounds; var scale = Math.Max(.01, Math.Min((ActualWidth - 128) / b.Width, (ActualHeight - 64) / b.Height));
        return new MatrixTransform(scale, 0, 0, scale, 64 - b.X * scale, 32 - b.Y * scale);
    }
    private Point Position(Inventory.AssetDto asset)
    {
        if (_dragId == asset.Id) return _drag;
        if (_vm?.SelectedRoom?.Kind == "corridor")
        {
            var b = CorridorMap.Drawing.Bounds; var p = CorridorTransform().Transform(new Point(b.X + asset.X!.Value * b.Width, b.Y + asset.Y!.Value * b.Height)); return new Point(p.X - 58, p.Y - 26);
        }
        return new Point(12 + asset.X!.Value * Math.Max(1, ActualWidth - 140), 12 + asset.Y!.Value * Math.Max(1, ActualHeight - 74));
    }
    private void Text(DrawingContext dc, string text, Point at, double size, double width)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Color("TextBrush", Brushes.Black), VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, at);
    }
    private async void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        var p = e.GetPosition(this); var match = _hitAreas.FirstOrDefault(x => x.Value.Contains(p)); if (match.Key is null) return;
        Focus();
        if (_vm.IsLinking) { await _vm.LinkOnMapAsync(match.Key); e.Handled = true; return; }
        _vm.SelectAsset(match.Key);
        if (e.ClickCount == 2) { _vm.OpenAssetCard(match.Key); e.Handled = true; return; }
        if (!_vm.CanEdit) return;
        _dragId = match.Key; _drag = match.Value.TopLeft; _down = p; _moved = false; CaptureMouse(); e.Handled = true;
    }
    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragId is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this); if (!_moved && (point - _down).Length < 5) return;
        _drag = new Point(Math.Clamp(point.X - 58, 0, Math.Max(0, ActualWidth - 116)), Math.Clamp(point.Y - 26, 0, Math.Max(0, ActualHeight - 52))); _moved = true; InvalidateVisual();
    }
    private async void OnUp(object sender, MouseButtonEventArgs e)
    {
        var id = _dragId; var p = _drag; var moved = _moved; _dragId = null; ReleaseMouseCapture();
        if (id is not null && moved && _vm is not null)
        {
            if (_vm.SelectedRoom?.Kind == "corridor")
            {
                var at = CorridorTransform().Inverse!.Transform(new Point(p.X + 58, p.Y + 26)); var b = CorridorMap.Drawing.Bounds;
                if (CorridorMap.Corridor.FillContains(at)) await _vm.PlaceAsync(id, (at.X - b.X) / b.Width, (at.Y - b.Y) / b.Height);
                else _vm.Message = "Выберите точку в голубой зоне коридора, за пределами кабинетов.";
            }
            else await _vm.PlaceAsync(id, (p.X - 12) / Math.Max(1, ActualWidth - 140), (p.Y - 12) / Math.Max(1, ActualHeight - 74));
        }
        InvalidateVisual();
    }
}
