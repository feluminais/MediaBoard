using Avalonia.Controls;
using CozyCouch.Desktop.ViewModels;

namespace CozyCouch.Desktop.Views;

public partial class TitleView : UserControl
{
    private TitleViewModel? _page;

    public TitleView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Listen(DataContext as TitleViewModel);
        DetachedFromVisualTree += (_, _) => Listen(null);
        AttachedToVisualTree += (_, _) => Listen(DataContext as TitleViewModel);
    }

    private void Listen(TitleViewModel? page)
    {
        if (ReferenceEquals(page, _page)) return;

        if (_page is not null) _page.ScrollRequested -= OnScrollRequested;
        _page = page;
        if (_page is not null) _page.ScrollRequested += OnScrollRequested;
    }

    // The viewer pressed an episode in the graph: its season is listed now, so bring its row into view.
    private void OnScrollRequested(EpisodeItem episode)
    {
        var at = _page?.Rows.IndexOf(episode) ?? -1;
        if (at >= 0) List.ScrollIntoView(at);
    }
}
