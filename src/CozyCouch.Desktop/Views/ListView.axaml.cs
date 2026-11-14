using Avalonia.Controls;
using CozyCouch.Desktop.Controls;

namespace CozyCouch.Desktop.Views;

/// <summary>The full list behind a Home row.</summary>
public partial class ListView : UserControl
{
    public ListView()
    {
        InitializeComponent();
        _ = new ScrollMemory(this, Scroller);
    }
}
