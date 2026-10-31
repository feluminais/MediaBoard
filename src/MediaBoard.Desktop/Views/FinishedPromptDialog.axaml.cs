using Avalonia.Controls;
using MediaBoard.Desktop.Controls;

namespace MediaBoard.Desktop.Views;

/// <summary>
/// Asked when a player that reports no progress closes. Closes with true (Yes) or false (No); closing the window
/// any other way answers nothing, and the app records nothing.
/// </summary>
public partial class FinishedPromptDialog : Window
{
    public FinishedPromptDialog()
        : this("")
    {
    }

    public FinishedPromptDialog(string title)
    {
        InitializeComponent();
        DialogFrame.Apply(this, Title ?? "MediaBoard");
        Question.Text = $"Did you finish {title}?";
        YesButton.Click += (_, _) => Close(true);
        NoButton.Click += (_, _) => Close(false);
    }
}
