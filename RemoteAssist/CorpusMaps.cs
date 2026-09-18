using System.Globalization;
using System.Reflection;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace RemoteAssist;

public sealed record MapRoom(string Number, Geometry Geometry, string Category);
public sealed record FloorMap(int Floor, double Width, double Height, DrawingGroup Drawing, IReadOnlyList<MapRoom> Rooms, Geometry Corridor);

/// <summary>Only SVG geometry is read from embedded university plans; HTML and scripts are never executed.</summary>
public static class CorpusMaps
{
    private static readonly Lazy<IReadOnlyList<FloorMap>> Maps = new(() => Enumerable.Range(1, 5).Select(Load).ToArray());
    public static IReadOnlyList<FloorMap> Floors => Maps.Value;
    private static double N(string? value, double fallback = 0) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    private static Geometry? Shape(XElement e)
    {
        string A(string name) => (string?)e.Attribute(name) ?? "";
        var x = N(A("x")); var y = N(A("y"));
        return e.Name.LocalName switch
        {
            "rect" => new RectangleGeometry(new Rect(x, y, N(A("width")), N(A("height")))),
            "circle" => new EllipseGeometry(new Point(N(A("cx")), N(A("cy"))), N(A("r")), N(A("r"))),
            "ellipse" => new EllipseGeometry(new Point(N(A("cx")), N(A("cy"))), N(A("rx")), N(A("ry"))),
            "line" => new LineGeometry(new Point(N(A("x1")), N(A("y1"))), new Point(N(A("x2")), N(A("y2")))),
            "path" when A("d").Length > 0 => Geometry.Parse(A("d")),
            "polygon" or "polyline" when A("points").Length > 0 => Geometry.Parse("M " + A("points") + (e.Name.LocalName == "polygon" ? " Z" : "")),
            _ => null
        };
    }
    private static Brush? Paint(string value) => value is "none" or "" ? null : (Brush)new BrushConverter().ConvertFromInvariantString(value)!;
    private static FloorMap Load(int floor)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream($"RemoteAssist.Maps.corpus-d-{floor}.svg") ?? throw new InvalidDataException("План этажа не найден.");
        using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var svg = XDocument.Load(reader).Root!;
        var styles = new Dictionary<string, Dictionary<string, string>>();
        foreach (var style in svg.Descendants().Where(x => x.Name.LocalName == "style"))
            foreach (Match match in Regex.Matches(style.Value, @"\.([\w]+)\s*\{([^}]+)\}"))
                styles[match.Groups[1].Value] = match.Groups[2].Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0].Trim(), x => x[1].Trim());
        var drawing = new DrawingGroup(); var rooms = new List<MapRoom>();
        var corridors = new GeometryGroup { FillRule = FillRule.Nonzero }; var obstacles = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var e in svg.Descendants())
        {
            var geometry = Shape(e); if (geometry is null) continue;
            var cls = (string?)e.Attribute("class") ?? "";
            var style = styles.GetValueOrDefault(cls) ?? new Dictionary<string, string>();
            var fill = Paint((string?)e.Attribute("fill") ?? style.GetValueOrDefault("fill", "#000000"));
            var stroke = Paint((string?)e.Attribute("stroke") ?? style.GetValueOrDefault("stroke", "none"));
            var pen = stroke is null ? null : new Pen(stroke, N(style.GetValueOrDefault("stroke-width"), 1));
            geometry.Freeze(); drawing.Children.Add(new GeometryDrawing(fill, pen, geometry));
            if (fill is SolidColorBrush color && color.Color == Color.FromRgb(225, 244, 253)) corridors.Children.Add(geometry);
            else if (fill is not null && e.Name.LocalName is "rect" or "polygon") obstacles.Children.Add(geometry);
            var parent = e.Parent;
            if ((string?)parent?.Attribute("class") == "main-block" && parent.Elements().FirstOrDefault(x => x.Name.LocalName is "rect" or "polygon" or "path") == e)
            {
                var id = (string?)parent.Attribute("id") ?? "";
                var number = Regex.IsMatch(id, @"^\d+[а-яА-Яa-zA-Z]*Д$") ? id[..^1] : "Комендант";
                rooms.Add(new MapRoom(number, geometry, cls));
            }
        }
        drawing.Freeze();
        foreach (var room in rooms) obstacles.Children.Add(room.Geometry);
        var corridor = new CombinedGeometry(GeometryCombineMode.Exclude, corridors, obstacles); corridor.Freeze();
        var bounds = ((string?)svg.Attribute("viewBox") ?? "0 0 900 345").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => N(x)).ToArray();
        return new FloorMap(floor, bounds[2], bounds[3], drawing, rooms, corridor);
    }
}

public sealed class FloorPlanControl : FrameworkElement
{
    public static readonly DependencyProperty FloorProperty = DependencyProperty.Register(nameof(Floor), typeof(int), typeof(FloorPlanControl), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedNumberProperty = DependencyProperty.Register(nameof(SelectedNumber), typeof(string), typeof(FloorPlanControl), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public int Floor { get => (int)GetValue(FloorProperty); set => SetValue(FloorProperty, value); }
    public string SelectedNumber { get => (string)GetValue(SelectedNumberProperty); set => SetValue(SelectedNumberProperty, value); }
    public event Action<string>? RoomClicked;
    public event Action? CorridorClicked;
    private FloorMap Map => CorpusMaps.Floors[Math.Clamp(Floor, 1, 5) - 1];
    private Rect MapBounds => Map.Drawing.Bounds;
    private double Scale => Math.Max(.01, Math.Min((ActualWidth - 16) / Math.Max(1, MapBounds.Width), (ActualHeight - 16) / Math.Max(1, MapBounds.Height)));
    public FloorPlanControl() { Cursor = Cursors.Hand; MouseLeftButtonDown += (_, e) => { var p = e.GetPosition(this); p = new Point((p.X - 8) / Scale + MapBounds.X, (p.Y - 8) / Scale + MapBounds.Y); var room = Map.Rooms.FirstOrDefault(x => x.Geometry.FillContains(p)); if (room is not null) RoomClicked?.Invoke(room.Number); else if (Map.Corridor.FillContains(p)) CorridorClicked?.Invoke(); }; }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.PushTransform(new TranslateTransform(8, 8)); dc.PushTransform(new ScaleTransform(Scale, Scale)); dc.PushTransform(new TranslateTransform(-MapBounds.X, -MapBounds.Y)); dc.DrawDrawing(Map.Drawing);
        var room = Map.Rooms.FirstOrDefault(x => x.Number == SelectedNumber);
        if (room is not null) dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(45, 0, 122, 204)), new Pen(Brushes.DodgerBlue, 2 / Scale), room.Geometry);
        else if (SelectedNumber.StartsWith("Коридор", StringComparison.OrdinalIgnoreCase)) dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(65, 0, 122, 204)), new Pen(Brushes.DodgerBlue, 1 / Scale), Map.Corridor);
        dc.Pop(); dc.Pop(); dc.Pop();
    }
}
