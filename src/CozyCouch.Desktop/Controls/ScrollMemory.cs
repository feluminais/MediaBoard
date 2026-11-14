using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CozyCouch.Desktop.ViewModels;

namespace CozyCouch.Desktop.Controls;

/// <summary>
/// Keeps a page's scroll position on its view model, so coming back to the page (Back from a title) lands where the
/// viewer left it instead of at the top. The shell builds a new view each time a page is shown; the view model is
/// the same.
/// </summary>
internal sealed class ScrollMemory
{
    private readonly Control _view;
    private readonly ScrollViewer _scroller;
    private bool _ready; // false until the saved position is back, so the layout's own scrolls are not saved

    public ScrollMemory(Control view, ScrollViewer scroller)
    {
        _view = view;
        _scroller = scroller;
        view.AttachedToVisualTree += (_, _) => Restore();
        view.DetachedFromVisualTree += (_, _) => _ready = false;
        scroller.ScrollChanged += (_, _) =>
        {
            if (_ready && _view.DataContext is PageViewModel page) page.ScrollOffset = _scroller.Offset.Y;
        };
    }

    // The rows are laid out first, then the offset can be set.
    private void Restore()
    {
        _ready = false;
        Dispatcher.UIThread.Post(() =>
        {
            if (_view.DataContext is PageViewModel { ScrollOffset: > 0 } page)
            {
                _view.UpdateLayout();
                _scroller.Offset = new Vector(0, page.ScrollOffset);
            }

            _ready = true;
        }, DispatcherPriority.Loaded);
    }
}
