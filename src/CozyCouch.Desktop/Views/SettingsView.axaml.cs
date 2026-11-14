using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CozyCouch.Desktop.ViewModels;

namespace CozyCouch.Desktop.Views;

public partial class SettingsView : UserControl
{
    private AccountViewModel? _account;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_account is not null) _account.PropertyChanged -= OnAccountChanged;
        _account = (DataContext as SettingsViewModel)?.Account;
        if (_account is not null) _account.PropertyChanged += OnAccountChanged;
        base.OnDataContextChanged(e);
    }

    // The password form opens and closes under the focus, and its Save button is disabled during a request. Either would
    // leave the keyboard in something hidden.
    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountViewModel.IsChangingPassword) || (e.PropertyName == nameof(AccountViewModel.IsBusy) && _account is { IsBusy: false }))
            FocusAccount();
    }

    // The controls show in the layout pass, so wait for it. Silent: no cue on code focus.
    private void FocusAccount() => Dispatcher.UIThread.Post(() =>
    {
        UpdateLayout();
        (_account is { IsChangingPassword: true } ? (Control)CurrentPasswordBox : ChangePasswordButton).Focus();
    }, DispatcherPriority.Loaded);
}
