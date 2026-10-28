using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;
    private InputElement? _focusBeforeSearch;

    public MainWindow()
    {
        InitializeComponent();
    }

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
