using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CozyCouch.Core.Library;
using CozyCouch.Desktop.Controls;
using CozyCouch.Desktop.ViewModels;
using CozyCouch.Desktop.Views;

namespace CozyCouch.Desktop;

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
    /// <param name="question">What to ask.</param>
    /// <param name="yes">The button that goes ahead.</param>
    /// <param name="danger">The answer can't easily be undone, so the button looks like the Remove it answers for.</param>
    public async Task<bool> ConfirmAsync(string question, string yes, bool danger = false)
    {
        if (ShownOwner() is not { } owner) return false;

        var dialog = new Window
        {
            Title = "CozyCouch",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var accept = new Button { Content = yes, Classes = { danger ? "danger" : "accent" }, MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true };
        accept.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24, 4, 24, 24),
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

        DialogFrame.Apply(dialog, "CozyCouch");
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>
    /// Asked on exit while a file plays: true for "Close it", false for "Leave it playing" (also Enter, Escape, and
    /// no window).
    /// </summary>
    public async Task<bool> AskClosePlayerAsync(string player)
    {
        if (ShownOwner() is not { } owner) return false;

        var dialog = new Window
        {
            Title = "CozyCouch",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var leave = new Button { Content = "Leave it playing", Classes = { "accent" }, MinWidth = 88, IsDefault = true, IsCancel = true };
        var close = new Button { Content = "Close it", MinWidth = 88 };
        leave.Click += (_, _) => dialog.Close(false);
        close.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24, 4, 24, 24),
            Spacing = 20,
            Children =
            {
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = $"{player} is still playing. Close it too?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = "If you leave it playing, CozyCouch won't save how far you get.",
                            Classes = { "muted" },
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { close, leave },
                },
            },
        };

        DialogFrame.Apply(dialog, "CozyCouch");
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>Lists the files that would go to the Recycle Bin; true when the user pressed Delete.</summary>
    public async Task<bool> ConfirmDeleteAsync(DeletePreview preview)
    {
        if (ShownOwner() is not { } owner) return false;

        return await new ConfirmDeleteDialog(preview).ShowDialog<bool>(owner);
    }

    /// <summary>"Did you finish ...?": true for Yes, false for No, null when the user closed it without answering.</summary>
    public async Task<bool?> AskFinishedAsync(string title)
    {
        if (ShownOwner() is not { } owner) return null;

        return await new FinishedPromptDialog(title).ShowDialog<bool?>(owner);
    }

    /// <summary>Shows a Sharing page panel; finishes when it has closed.</summary>
    public async Task ShowSharingPanelAsync(SharingPanel panel)
    {
        if (ShownOwner() is not { } owner) return;

        await new SharingDialog(panel).ShowDialog(owner);
    }

    /// <summary>The Connect screen, over the shell; finishes when it has closed. Success restarts the app.</summary>
    public async Task ShowConnectAsync(ConnectViewModel connect)
    {
        if (ShownOwner() is not { } owner) return;

        await new ConnectDialog(connect).ShowDialog(owner);
    }

    /// <summary>Puts text on the clipboard.</summary>
    public async Task CopyAsync(string text)
    {
        if (Owner?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    // The window may be hidden in the tray or never have opened, and a dialog can't open over a hidden owner.
    private Window? ShownOwner()
    {
        if (Owner is { IsVisible: false } hidden) hidden.Show();
        return Owner;
    }

    /// <summary>Opens a web address in the default browser. Only https addresses.</summary>
    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
