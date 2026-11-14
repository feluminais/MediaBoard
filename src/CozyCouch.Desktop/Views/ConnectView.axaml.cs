using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CozyCouch.Desktop.ViewModels;

namespace CozyCouch.Desktop.Views;

/// <summary>Joining a server. The field to fill in has the focus at each step, and again after a refused try.</summary>
public partial class ConnectView : UserControl
{
    private ConnectViewModel? _vm;

    public ConnectView()
    {
        InitializeComponent();

        // Tunnelling, so Enter sends rather than breaking the line. Shift+Enter is still a line break.
        InviteBox.AddHandler(KeyDownEvent, OnInviteKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Puts the caret in the field this step wants filled in. Silent: no cue on code focus.</summary>
    public void FocusField() => Dispatcher.UIThread.Post(() =>
    {
        UpdateLayout();
        NextField()?.Focus();
    }, DispatcherPriority.Loaded);

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as ConnectViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        base.OnDataContextChanged(e);
    }

    private TextBox? NextField() => _vm?.Step switch
    {
        ConnectStep.Invite => InviteBox,
        ConnectStep.NewPassword or ConnectStep.KnownPassword => PasswordBox,
        ConnectStep.SignIn => string.IsNullOrEmpty(_vm.Name) ? NameBox : PasswordBox,
        _ => null,
    };

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A pressed button is disabled during the request, which leaves the focus nowhere.
        if (e.PropertyName == nameof(ConnectViewModel.Step) || (e.PropertyName == nameof(ConnectViewModel.IsBusy) && _vm is { IsBusy: false }))
            FocusField();
    }

    private void OnInviteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;

        e.Handled = true;
        if (_vm is { } vm && vm.ContinueCommand.CanExecute(null)) vm.ContinueCommand.Execute(null);
    }
}
