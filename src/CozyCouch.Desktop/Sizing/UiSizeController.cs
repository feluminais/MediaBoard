using Avalonia.Controls;
using Avalonia.Threading;
using CozyCouch.Core.Settings;

namespace CozyCouch.Desktop.Sizing;

/// <summary>
/// Keeps the size resources in step with the Display settings, the main window's width and its display scaling. It
/// writes them only when a size actually changes, so dragging the window edge rewrites nothing between steps.
/// </summary>
public sealed class UiSizeController
{
    private readonly Window _window;
    private readonly IResourceDictionary _resources;
    private AppSettings _settings;
    private UiSizes? _applied;
    private double _appliedScaling;

    public UiSizeController(Window window, SettingsStore store, IResourceDictionary resources)
    {
        _window = window;
        _resources = resources;
        _settings = store.Load();
        store.Changed += saved => Dispatcher.UIThread.Post(() =>
        {
            _settings = saved;
            Refresh();
        });
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ClientSizeProperty) Refresh();
        };
        window.ScalingChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        var sizes = UiSizes.For(_settings, _window.ClientSize.Width);
        var scaling = _window.RenderScaling;
        if (sizes == _applied && scaling == _appliedScaling) return;

        sizes.Apply(_resources, scaling);
        _applied = sizes;
        _appliedScaling = scaling;
    }
}
