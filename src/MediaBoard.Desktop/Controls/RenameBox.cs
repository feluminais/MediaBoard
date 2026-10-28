using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MediaBoard.Desktop.Controls;

/// <summary>
/// The box an item is renamed in. It looks like a <see cref="TextBox"/> and takes the focus, with the text selected,
/// each time it appears, so the viewer can type at once.
/// </summary>
public sealed class RenameBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.Name != nameof(IsEffectivelyVisible) || !IsEffectivelyVisible) return;

        // The box is visible in this pass but not laid out yet: focus after it.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsEffectivelyVisible) return;
            Focus();
            SelectAll();
        });
    }
}
