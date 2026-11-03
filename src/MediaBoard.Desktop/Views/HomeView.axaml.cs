using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MediaBoard.Desktop.Views;

public partial class HomeView : UserControl
{
    private const double WheelStep = 120;
    private const double Edge = 1;

    public HomeView()
    {
        InitializeComponent();
        // Tunnel, so the row sees the wheel before the page's own scroll viewer does.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    // The wheel scrolls the page; a row only takes over once the page has run out of room below,
    // so the row reads as the page's last stretch of scrolling, just sideways.
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Delta.Y == 0) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ScrollViewer>(includeSelf: true) is not { } row || !row.Classes.Contains("row")) return;

        var rowMax = row.Extent.Width - row.Viewport.Width;
        var pageMax = Page.Extent.Height - Page.Viewport.Height;
        if (!RowTakesWheel(e.Delta.Y, row.Offset.X, rowMax, Page.Offset.Y, pageMax)) return;

        row.Offset = row.Offset.WithX(Math.Clamp(row.Offset.X - e.Delta.Y * WheelStep, 0, rowMax));
        e.Handled = true;
    }

    // Down: the row scrolls only with the page at its bottom and the row not yet at its end.
    // Up: a row that was scrolled sideways comes back to its start first; from there the page scrolls up.
    public static bool RowTakesWheel(double deltaY, double rowX, double rowMax, double pageY, double pageMax)
    {
        if (rowMax <= Edge) return false;
        if (deltaY > 0) return rowX > Edge;
        return pageMax - pageY <= Edge && rowX < rowMax - Edge;
    }
}
