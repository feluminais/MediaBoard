using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

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
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        base.OnDataContextChanged(e);
    }

    // Opening the palette remembers what had the focus and puts the caret in its box (the box becomes visible in the
    // same pass, so focus after it). Closing gives the focus back.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
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
            Dispatcher.UIThread.Post(() => back?.Focus());
        }
    }

    // Only a click on the dimmed area itself closes the palette, not one that bubbled up from the palette.
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, SearchBackdrop)) _vm?.CloseSearchCommand.Execute(null);
    }
}
