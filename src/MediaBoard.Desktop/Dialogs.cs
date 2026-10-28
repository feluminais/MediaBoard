using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MediaBoard.Core.Library;
using MediaBoard.Desktop.Views;

namespace MediaBoard.Desktop;

/// <summary>
/// The system dialogs the pages need: folder and program pickers, yes or no questions (including the delete confirmation and "did you finish?"), and the default browser.
/// The main window sets <see cref="Owner"/> once it exists; until then (and in tests) every dialog answers "no".
/// </summary>
public sealed class Dialogs
{
    private static readonly FilePickerFileType Programs = new("Programs") { Patterns = ["*.exe"] };

    public Window? Owner { get; set; }

    /// <summary>The folder the user picked, or null when they cancelled.</summary>
    public async Task<string?> PickFolderAsync()
    {
        if (Owner?.StorageProvider is not { } storage) return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add folder", AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    /// <summary>The program (an .exe) the user picked, or null when they cancelled.</summary>
    public async Task<string?> PickProgramAsync()
    {
        if (Owner?.StorageProvider is not { } storage) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a player",
            AllowMultiple = false,
            FileTypeFilter = [Programs],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    /// <summary>Asks <paramref name="question"/> with two buttons; true when the user pressed <paramref name="yes"/>.</summary>
    public async Task<bool> ConfirmAsync(string question, string yes)
    {
        if (Owner is null) return false;

        var dialog = new Window
        {
            Title = "MediaBoard",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var accept = new Button { Content = yes, Classes = { "accent" }, MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true };
        accept.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = question, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, accept },
                },
            },
        };

        return await dialog.ShowDialog<bool>(Owner);
    }

    /// <summary>Lists the files that would go to the Recycle Bin; true when the user pressed Delete.</summary>
    public async Task<bool> ConfirmDeleteAsync(DeletePreview preview)
    {
        if (Owner is null) return false;

        return await new ConfirmDeleteDialog(preview).ShowDialog<bool>(Owner);
    }

    /// <summary>"Did you finish ...?": true for Yes, false for No, null when the user closed it without answering.</summary>
    public async Task<bool?> AskFinishedAsync(string title)
    {
        if (Owner is null) return null;

        return await new FinishedPromptDialog(title).ShowDialog<bool?>(Owner);
    }

    /// <summary>Opens a web address in the default browser. Only https addresses.</summary>
    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
