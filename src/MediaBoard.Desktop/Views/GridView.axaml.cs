using Avalonia.Controls;
using Avalonia.Input;
using MediaBoard.Desktop.Controls;

namespace MediaBoard.Desktop.Views;

/// <summary>The Movies and Shows poster grid.</summary>
public partial class GridView : UserControl
{
    // A row of cards: views this far outside the viewport still count as shown (the repeater realizes a little beyond it).
    private const double ViewportSlack = 304;

    public GridView()
    {
        InitializeComponent();

        // The repeater keeps the cards it scrolled past in a pool, still in the tree, and may keep an early view
        // realized: tell the cards, so they stop asking for posters and let go of their bitmaps until they are
        // shown again.
        Cards.ElementClearing += (_, e) => (e.Element as PosterHost)?.Park();
        Cards.ElementPrepared += (_, e) => (e.Element as PosterHost)?.Unpark();
        AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => SyncViewport());
        Cards.LayoutUpdated += (_, _) => SyncViewport();
        _ = new ScrollMemory(this, Scroller);
    }

    // Escape in the filter clears what was typed; with nothing typed it is left for the layers outside.
    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || sender is not TextBox { Text.Length: > 0 } box) return;

        box.Text = "";
        e.Handled = true;
    }

    private void SyncViewport()
    {
        if (Scroller is not { } scroller) return;

        var top = scroller.Offset.Y - ViewportSlack;
        var bottom = scroller.Offset.Y + scroller.Viewport.Height + ViewportSlack;
        foreach (var child in Cards.Children)
        {
            if (child is PosterHost host) host.SetOffScreen(child.Bounds.Bottom < top || child.Bounds.Top > bottom);
        }
    }
}
