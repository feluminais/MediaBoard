using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

/// <summary>
/// The "Fix match" search. Its owner shows it by giving it a view model and hides it by taking the view model away
/// (the view stays in the tree, hidden). Opening puts the caret in the search box; closing gives the focus back to
/// what had it before, normally the button that opened the search.
/// </summary>
public partial class FixMatchPickerView : UserControl
{
    private InputElement? _opener;

    public FixMatchPickerView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this)?.FocusManager is not { } focus) return;

        if (DataContext is FixMatchPickerViewModel)
        {
            // Opened by a press on a button, which still has the focus. The box becomes visible in the same pass, so
            // focus after it. Silent: Focus() with no arguments shows no cue.
            _opener = focus.GetFocusedElement() as InputElement;
            Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Loaded);
            return;
        }

        var opener = _opener;
        _opener = null;
        if (opener is null) return;

        // Hiding the search takes the focus with it. Give it back, unless it has already moved somewhere visible.
        Dispatcher.UIThread.Post(() =>
        {
            if (focus.GetFocusedElement() is InputElement { IsEffectivelyVisible: true }) return;
            if (opener is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true }) opener.Focus();
        });
    }
}
