using Avalonia.Controls;
using MediaBoard.Desktop.Controls;

namespace MediaBoard.Desktop.Views;

/// <summary>The full list behind a Home row.</summary>
public partial class ListView : UserControl
{
    public ListView()
    {
        InitializeComponent();
        _ = new ScrollMemory(this, Scroller);
    }
}
