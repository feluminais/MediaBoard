using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MediaBoard.Desktop.Controls;

/// <summary>Where the focus goes when what had it is gone.</summary>
internal static class FocusTargets
{
    /// <summary>
    /// The first control under <paramref name="scope"/> the viewer can Tab to and press. A text box is skipped: the
    /// focus landing in one would send the next typed letters into it.
    /// </summary>
    public static InputElement? First(Visual? scope) =>
        scope?.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(c => c is not TextBox && c is { Focusable: true, IsTabStop: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true });
}
