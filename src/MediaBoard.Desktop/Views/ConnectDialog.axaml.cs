using Avalonia.Controls;
using Avalonia.Input;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

/// <summary>The Connect screen over the shell, for a PC on its own library. Back on the first step closes it, and that changes nothing.</summary>
public partial class ConnectDialog : Window
{
    public ConnectDialog()
    {
        InitializeComponent();
    }

    public ConnectDialog(ConnectViewModel connect)
        : this()
    {
        DialogFrame.Apply(this, Title ?? "MediaBoard");
        Connect.DataContext = connect;
        connect.Left += Close;
        Opened += (_, _) => Connect.FocusField();
        Closed += (_, _) => connect.Left -= Close;
    }

    private void OnRootPressed(object? sender, PointerPressedEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) Root.Focus();
    }
}
