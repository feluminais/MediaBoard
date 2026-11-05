using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

public partial class SharingView : UserControl
{
    private SharingViewModel? _vm;

    public SharingView()
    {
        InitializeComponent();
        _ = new ScrollMemory(this, Scroller);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm = DataContext as SharingViewModel;
        if (_vm is null) return;

        _vm.ConnectionNeeded += OnConnectionNeeded;
        _vm.RowsRemoved += OnRowsRemoved;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is null) return;

        _vm.ConnectionNeeded -= OnConnectionNeeded;
        _vm.RowsRemoved -= OnRowsRemoved;
        _vm = null;
    }

    // The fields just became visible, so they can take the focus once laid out.
    private void OnConnectionNeeded() => Dispatcher.UIThread.Post(() =>
    {
        PublicBox.Focus();
        PublicBox.BringIntoView();
    }, DispatcherPriority.Loaded);

    // What had the focus may have gone with the row. The next row's header takes it, or else the next stream's Stop.
    private void OnRowsRemoved(PersonRow? near) => Dispatcher.UIThread.Post(() =>
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is InputElement { IsEffectivelyVisible: true, IsTabStop: true }) return;

        UpdateLayout();
        var target = (near is null ? null : HeaderOf(near)) ?? FocusTargets.First(WatchingList) ?? Switch;
        target.Focus();
    }, DispatcherPriority.Loaded);

    private Button? HeaderOf(PersonRow row) =>
        PeopleList.ContainerFromItem(row)?.GetVisualDescendants().OfType<Button>().FirstOrDefault();
}
