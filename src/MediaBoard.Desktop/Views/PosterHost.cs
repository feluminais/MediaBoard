using Avalonia;
using Avalonia.Controls;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

/// <summary>
/// Base of the views that show a <see cref="PosterCardViewModel"/>. A virtualized panel builds a view only for a card
/// that is on screen and tears it down when it scrolls off, so asking for the poster on attach and cancelling on
/// detach means only the visible cards (and the page that is open) ever look for pictures.
/// </summary>
public abstract class PosterHost : UserControl
{
    private PosterCardViewModel? _watching;
    private bool _attached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Watch(DataContext as PosterCardViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Watch(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_attached) Watch(DataContext as PosterCardViewModel); // a recycled view gets another card
    }

    private void Watch(PosterCardViewModel? card)
    {
        if (ReferenceEquals(_watching, card)) return;

        _watching?.CancelPoster();
        _watching = card;
        card?.RequestPoster();
    }
}
