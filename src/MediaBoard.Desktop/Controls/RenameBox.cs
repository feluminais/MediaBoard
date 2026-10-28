using Avalonia;
using Avalonia.Controls;

namespace MediaBoard.Desktop.Controls;

/// <summary>
/// The box an item is renamed in. It looks like a <see cref="TextBox"/> and takes the focus, with the text selected,
/// each time it appears, so the viewer can type at once (see <see cref="ResumeEdit"/>).
/// </summary>
public sealed class RenameBox : TextBox
{
    /// <summary>
    /// The edit is being resumed after the page rebuilt its rows: the box takes the focus and puts the caret after the
    /// text, so the next keystroke adds to it instead of replacing a selection.
    /// </summary>
    public static readonly StyledProperty<bool> ResumeEditProperty =
        AvaloniaProperty.Register<RenameBox, bool>(nameof(ResumeEdit));

    public bool ResumeEdit
    {
        get => GetValue(ResumeEditProperty);
        set => SetValue(ResumeEditProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

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
        if (ResumeEdit) CaretIndex = Text?.Length ?? 0;
        else SelectAll();
    }
}

file static class RectExtensions
{
    public static bool IsEmpty(this Rect rect) => rect.Width <= 0 || rect.Height <= 0;
}
