using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CozyCouch.Desktop.Controls;
using CozyCouch.Desktop.ViewModels;

namespace CozyCouch.Desktop.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;
    private InputElement? _focusBeforeSearch;

    private const string MaximizeIcon = "M5.5 5.5 H14.5 V14.5 H5.5 Z";
    private const string RestoreIcon = "M7.5 5.5 H14.5 V12.5 M5.5 7.5 H12.5 V14.5 H5.5 Z";

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;

        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = Geometry.Parse(maximized ? RestoreIcon : MaximizeIcon);
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximise");
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            if (_vm.Connection is { } left) left.PropertyChanged -= OnConnectionChanged;
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            if (_vm.Connection is { } connection) connection.PropertyChanged += OnConnectionChanged;
        }

        base.OnDataContextChanged(e);
    }

    // The panel can stay while its button goes: a retry that connected to an empty library.
    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionPanelViewModel.HasAction)) FocusNewPage();
    }

    // Opening the palette remembers what had the focus and puts the caret in its box (the box becomes visible in the
    // same pass, so focus after it). Closing gives the focus back.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.CurrentPage) or nameof(MainWindowViewModel.IsPageCovered))
        {
            FocusNewPage();
            return;
        }

        if (e.PropertyName != nameof(MainWindowViewModel.IsSearchOpen) || _vm is null) return;

        if (_vm.IsSearchOpen)
        {
            _focusBeforeSearch = FocusManager?.GetFocusedElement() as InputElement;
            Dispatcher.UIThread.Post(() => Palette.FocusInput());
        }
        else
        {
            var back = _focusBeforeSearch;
            _focusBeforeSearch = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (back is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true }) back.Focus();
                else FocusManager?.FindFirstFocusableElement()?.Focus();
            });
        }
    }

    // The page that was open takes its focused control with it (the card that was pressed, a Back button, a row's
    // button), and the focus would be nowhere. Put it on the new page's first control, silently (no cue, no tooltip),
    // unless it is already somewhere visible: the sidebar button that was pressed keeps it. The new page's rows are
    // built in the layout pass, so wait for it. The connection panel going over a page, or off it, is the same thing: what
    // had the focus is out of sight, and keys must not go on reaching it. A panel with no button leaves the root, which
    // keeps Ctrl+F working.
    private void FocusNewPage()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (FocusManager?.GetFocusedElement() is InputElement { IsEffectivelyVisible: true }) return;

            var shown = _vm is { IsPageCovered: true } ? (Control)ConnectionHost : PageHost;
            shown.UpdateLayout();
            (FocusTargets.First(shown) ?? Root).Focus();
        }, DispatcherPriority.Loaded);
    }

    // Escape closes the palette when focus is not in it (inside, the palette takes the key first). A bubbling handler,
    // not a KeyBinding: a binding would run before every handler below the window.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || _vm is not { IsSearchOpen: true }) return;

        _vm.CloseSearchCommand.Execute(null);
        e.Handled = true;
    }

    // Only a click on the dimmed area itself closes the palette, not one that bubbled up from the palette.
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, SearchBackdrop)) return;

        _vm?.CloseSearchCommand.Execute(null);
        e.Handled = true;
    }

    // A press no control handled (TextBox and Button handle their own) ends a text edit: the caret and the box's focus
    // styling go. Focus moves to the root, never to nothing, so Ctrl+F and Escape keep working.
    private void OnRootPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm?.IsSearchOpen == true) return;
        if (FocusManager?.GetFocusedElement() is TextBox) Root.Focus();
    }
}
