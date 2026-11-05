using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

/// <summary>The Sharing page's modal panel. It shows the panel's step, and the focus starts on each step's first control.</summary>
public partial class SharingDialog : Window
{
    private readonly SharingPanel _panel;

    public SharingDialog()
        : this(new SharingPanel())
    {
    }

    public SharingDialog(SharingPanel panel)
    {
        InitializeComponent();
        DialogFrame.Apply(this, Title ?? "MediaBoard");
        _panel = panel;
        DataContext = panel;
        panel.CloseRequested += Close;
        panel.PropertyChanged += OnPanelChanged;
        Opened += (_, _) => FocusStep();
        Closed += (_, _) =>
        {
            panel.CloseRequested -= Close;
            panel.PropertyChanged -= OnPanelChanged;
        };
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SharingPanel.Step) && _panel.Step is not null) FocusStep();
    }

    private void OnRootPressed(object? sender, PointerPressedEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) Root.Focus();
    }

    // The step's controls are built in the layout pass, so wait for it. Silent: no cue on code focus.
    private void FocusStep() => Dispatcher.UIThread.Post(() =>
    {
        UpdateLayout();
        this.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(c => c is { Focusable: true, IsTabStop: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
            ?.Focus();
    }, DispatcherPriority.Loaded);
}
