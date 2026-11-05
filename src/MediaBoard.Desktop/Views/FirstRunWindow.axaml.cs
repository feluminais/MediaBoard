using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

/// <summary>What opens before the app knows whose library to show. It owns its view model, which holds what was typed.</summary>
public partial class FirstRunWindow : Window
{
    private FirstRunViewModel? _vm;

    public FirstRunWindow()
    {
        InitializeComponent();
        DialogFrame.Apply(this, Title ?? "MediaBoard");
        Opened += (_, _) => FocusStep();
        Closed += (_, _) =>
        {
            if (_vm is not { } vm) return;

            vm.PropertyChanged -= OnViewModelChanged;
            vm.Dispose();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as FirstRunViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        base.OnDataContextChanged(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FirstRunViewModel.Step)) FocusStep();
    }

    private void OnRootPressed(object? sender, PointerPressedEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) Root.Focus();
    }

    // The step's controls show in the layout pass, so wait for it. Silent: no cue on code focus.
    private void FocusStep() => Dispatcher.UIThread.Post(() =>
    {
        UpdateLayout();
        switch (_vm?.Step)
        {
            case StartStep.Connect:
                ConnectPanel.FocusField();
                break;
            case StartStep.SettingsProblem:
                TryAgainButton.Focus();
                break;
            default:
                AddFolderChoice.Focus();
                break;
        }
    }, DispatcherPriority.Loaded);
}
