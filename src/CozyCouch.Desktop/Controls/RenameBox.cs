using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace CozyCouch.Desktop.Controls;

/// <summary>
/// The box an item is renamed in. It looks like a <see cref="TextBox"/> and takes the focus, with the text selected,
/// each time it appears, so the viewer can type at once. When the edit ends (<see cref="Editing"/> goes false: Enter,
/// Escape, Save or Cancel hide the box and its buttons) the focus goes back to what had it when the edit began, the
/// button that was pressed to start it, instead of being lost with the hidden controls.
/// </summary>
public sealed class RenameBox : TextBox
{
    /// <summary>The item is being renamed. Bound to the item's <c>IsEditing</c>.</summary>
    public static readonly StyledProperty<bool> EditingProperty =
        AvaloniaProperty.Register<RenameBox, bool>(nameof(Editing));

    private InputElement? _opener;

    public bool Editing
    {
        get => GetValue(EditingProperty);
        set => SetValue(EditingProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    static RenameBox()
    {
        EditingProperty.Changed.AddClassHandler<RenameBox>((box, e) => box.OnEditingChanged(e.GetNewValue<bool>()));
    }

    public RenameBox()
    {
        // A box inside a hidden panel has no size; it gets one in the layout pass that shows it. (IsEffectivelyVisible
        // is not reported through PropertyChanged, so the size is the signal.)
        PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Bounds) && e.GetOldValue<Rect>().IsEmpty() && !Bounds.IsEmpty()) FocusNow();
        };
    }

    private void FocusNow()
    {
        Focus();
        SelectAll();
    }

    // The edit begins from a press on the item's Rename button, which still has the focus at this point.
    private void OnEditingChanged(bool editing)
    {
        var focus = TopLevel.GetTopLevel(this)?.FocusManager;
        if (editing)
        {
            _opener = focus?.GetFocusedElement() as InputElement;
            return;
        }

        var opener = _opener;
        _opener = null;
        if (focus is null || opener is null) return;

        // Hiding the box takes the focus with it. Give it back, unless the viewer has already moved it somewhere visible.
        Dispatcher.UIThread.Post(() =>
        {
            if (focus.GetFocusedElement() is InputElement { IsEffectivelyVisible: true }) return;
            if (opener is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true }) opener.Focus();
        });
    }
}

file static class RectExtensions
{
    public static bool IsEmpty(this Rect rect) => rect.Width <= 0 || rect.Height <= 0;
}
