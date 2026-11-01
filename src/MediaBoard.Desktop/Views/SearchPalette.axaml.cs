using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

public partial class SearchPalette : UserControl
{
    private SearchPaletteViewModel? _vm;

    public SearchPalette()
    {
        InitializeComponent();

        // The arrows, Enter and Escape belong to the palette, whatever the box would do with them.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Puts the caret in the box, with what it holds selected.</summary>
    public void FocusInput()
    {
        Input.Focus();
        Input.SelectAll();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as SearchPaletteViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        base.OnDataContextChanged(e);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.KeyModifiers != KeyModifiers.None) return;

        switch (e.Key)
        {
            case Key.Down:
                _vm.MoveDownCommand.Execute(null);
                break;
            case Key.Up:
                _vm.MoveUpCommand.Execute(null);
                break;
            case Key.Enter:
                _vm.OpenSelectedCommand.Execute(null);
                break;
            case Key.Escape:
                _vm.EscapeCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // A long list scrolls: keep the selected row in view. The rows are laid out after the change, so wait for that.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SearchPaletteViewModel.SelectedIndex) || _vm is null) return;

        var index = _vm.SelectedIndex;
        Dispatcher.UIThread.Post(() =>
        {
            if (index >= 0) this.FindControl<ItemsControl>("RowList")!.ContainerFromIndex(index)?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
