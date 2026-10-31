using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace MediaBoard.Desktop.Controls;

/// <summary>
/// The root of every dialog: a title strip and close button drawn by the app, over the dialog's own content. The
/// window keeps the system's border and resize edge but not its title bar. The look is in App.axaml.
/// </summary>
public class DialogFrame : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DialogFrame, string?>(nameof(Title));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Puts <paramref name="window"/> into the app's dialog chrome, around the content it already has.</summary>
    public static void Apply(Window window, string title)
    {
        window.WindowDecorations = WindowDecorations.BorderOnly;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = 40;
        var content = window.Content;
        window.Content = null;
        window.Content = new DialogFrame { Title = title, Content = content };
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Button>("PART_Close") is { } close)
            close.Click += OnCloseClick;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as Window)?.Close();
}
