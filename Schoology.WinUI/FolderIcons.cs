using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Schoology.WinUI;

internal static class FolderIcons
{
    internal static SymbolIcon CreateIcon(string color, double size = 25)
    {
        return new SymbolIcon(Symbol.Folder)
        {
            Width = size,
            Height = size,
            Foreground = new SolidColorBrush(GetColor(color)),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        };
    }

    private static Windows.UI.Color GetColor(string color) => color.Trim().ToLowerInvariant() switch
    {
        "black" => Windows.UI.Color.FromArgb(255, 76, 82, 90),
        "blue" => Windows.UI.Color.FromArgb(255, 48, 126, 204),
        "gray" or "grey" => Windows.UI.Color.FromArgb(255, 126, 137, 150),
        "green" => Windows.UI.Color.FromArgb(255, 49, 153, 105),
        "orange" => Windows.UI.Color.FromArgb(255, 218, 130, 43),
        "pink" => Windows.UI.Color.FromArgb(255, 211, 102, 151),
        "purple" => Windows.UI.Color.FromArgb(255, 139, 100, 190),
        "red" => Windows.UI.Color.FromArgb(255, 205, 78, 78),
        "yellow" => Windows.UI.Color.FromArgb(255, 190, 150, 48),
        _ => Windows.UI.Color.FromArgb(255, 126, 137, 150)
    };
}
