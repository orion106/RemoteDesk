using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace RemoteAssist;

public static class ThemeManager
{
    public static void Apply(Window window, string theme)
    {
        var dark = theme != "Светлая";
        if (theme == "System")
        {
            try { dark = (int?)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0; }
            catch { dark = true; }
        }
        var values = new Dictionary<string, string>
        {
            ["CanvasBrush"] = dark ? "#1E1E1E" : "#F4F5F8",
            ["PanelBrush"] = dark ? "#2D2D30" : "#FFFFFF",
            ["SidebarBrush"] = dark ? "#252527" : "#EBEEF3",
            ["InputBrush"] = dark ? "#232325" : "#F7F8FA",
            ["HoverBrush"] = dark ? "#38383C" : "#E8ECF2",
            ["LineBrush"] = dark ? "#424247" : "#D9DEE7",
            ["TextBrush"] = dark ? "#F5F5F7" : "#1E293B",
            ["MutedBrush"] = dark ? "#AFAFB9" : "#5D687A",
            ["AccentSoftBrush"] = dark ? "#18384C" : "#DEEFFB",
            ["WarningBrush"] = dark ? "#EDBA75" : "#926115",
            ["SuccessBrush"] = dark ? "#76CBA2" : "#23734E"
        };
        foreach (var (key, value) in values)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            window.Resources[key] = brush;
        }
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            var useDarkCaption = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref useDarkCaption, sizeof(int));
        }
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
