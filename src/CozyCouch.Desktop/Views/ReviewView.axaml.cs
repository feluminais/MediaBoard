using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CozyCouch.Desktop.Controls;
using CozyCouch.Desktop.ViewModels;

namespace CozyCouch.Desktop.Views;

public partial class ReviewView : UserControl
{
    private ReviewViewModel? _vm;

    public ReviewView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null) _vm.Items.CollectionChanged -= OnItemsChanged;
        _vm = DataContext as ReviewViewModel;
        if (_vm is not null) _vm.Items.CollectionChanged += OnItemsChanged;
        base.OnDataContextChanged(e);
    }

    // A row the viewer settled (picked, or "Not a movie or show") leaves the list with the button they pressed. The
    // focus goes to the row that took its place, or the nearest one built, so Tab goes on from there.
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Remove || _vm is null) return;

        var at = e.OldStartingIndex;
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager is not { } focus) return;
            if (focus.GetFocusedElement() is InputElement { IsEffectivelyVisible: true }) return;

            List.UpdateLayout();
            var count = _vm.Items.Count;
            var target = Enumerable.Range(Math.Min(at, count), Math.Max(0, count - at))
                .Concat(Enumerable.Range(0, Math.Min(at, count)).Reverse())
                .Select(i => FocusTargets.First(List.ContainerFromIndex(i)))
                .FirstOrDefault(t => t is not null);
            (target ?? focus.FindFirstFocusableElement())?.Focus();
        }, DispatcherPriority.Loaded);
    }
}
