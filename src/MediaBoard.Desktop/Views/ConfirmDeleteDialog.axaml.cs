using Avalonia.Controls;
using MediaBoard.Core.Library;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

/// <summary>
/// The confirmation before "Delete from disk": it lists every file and warns about torrent clients. Closes with true
/// only when the viewer presses Delete; Cancel is the default button, so Enter never deletes.
/// </summary>
public partial class ConfirmDeleteDialog : Window
{
    public ConfirmDeleteDialog()
        : this(new DeletePreview(0, "", []))
    {
    }

    public ConfirmDeleteDialog(DeletePreview preview)
    {
        InitializeComponent();
        DialogFrame.Apply(this, Title ?? "MediaBoard");
        DataContext = new ConfirmDeleteViewModel(preview);
        DeleteButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
