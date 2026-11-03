using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

/// <summary>
/// Base of the views that show a <see cref="PosterCardViewModel"/>. A card looks for its poster only while its view is
/// shown: attached to a window, visible, and not parked in a panel's recycle pool. A panel that recycles views may keep
/// them in the tree (an <c>ItemsRepeater</c> pool, a hidden container), so leaving the tree is not the only signal;
/// the grid calls <see cref="Park"/>, <see cref="Unpark"/> and <see cref="SetOffScreen"/>.
/// </summary>
public abstract class PosterHost : UserControl
{
    private PosterCardViewModel? _watching;
    private bool _attached;
    private bool _parked;
    private bool _offScreen;

    protected PosterHost()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(IsEffectivelyVisible)) Update();
        };

        // Only a Continue watching card has a menu (to remove it); a right-click on any other card opens nothing.
        AddHandler(ContextRequestedEvent, (_, e) =>
        {
            if (DataContext is not PosterCardViewModel { CanRemove: true }) e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private bool Shown => _attached && !_parked && !_offScreen && IsEffectivelyVisible;

    /// <summary>The panel parked this view for reuse: cancel the poster request and let go of the bitmap.</summary>
    public void Park() => Set(ref _parked, true);

    /// <summary>The panel is showing this view again, for the card it now has.</summary>
    public void Unpark() => Set(ref _parked, false);

    /// <summary>
    /// The view is built but outside the scroll viewport (a panel may keep a view it realized earlier, such as the
    /// first one, until its position is recycled): same effect as <see cref="Park"/>.
    /// </summary>
    public void SetOffScreen(bool offScreen) => Set(ref _offScreen, offScreen);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Update();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Update(); // a recycled view gets another card
    }

    private void Set(ref bool flag, bool value)
    {
        if (flag == value) return;

        flag = value;
        var hidden = _parked || _offScreen;
        foreach (var image in this.GetVisualDescendants().OfType<PosterImage>()) image.Suspended = hidden;
        Update();
    }

    private void Update()
    {
        var card = Shown ? DataContext as PosterCardViewModel : null;
        if (ReferenceEquals(_watching, card)) return;

        _watching?.CancelPoster();
        _watching = card;
        card?.RequestPoster();
    }
}
