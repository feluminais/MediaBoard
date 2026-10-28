using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MediaBoard.Desktop.Views;

public partial class HomeView : UserControl
{
    private const double WheelStep = 120;

    public HomeView()
    {
        InitializeComponent();
        // Tunnel, so the row sees the wheel before the page's own scroll viewer does.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    // The wheel over a row that overflows scrolls the row sideways; elsewhere it scrolls the page.
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Delta.Y == 0) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ScrollViewer>(includeSelf: true) is not { } row || !row.Classes.Contains("row")) return;

        var most = row.Extent.Width - row.Viewport.Width;
        if (most <= 0) return;

        row.Offset = row.Offset.WithX(Math.Clamp(row.Offset.X - e.Delta.Y * WheelStep, 0, most));
        e.Handled = true;
    }
}
