using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;

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

    // Opening search puts the caret in the box; the box becomes visible in the same pass, so focus after it.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSearchOpen) && _vm is { IsSearchOpen: true })
            Dispatcher.UIThread.Post(() => SearchBox.Focus());
    }
}
