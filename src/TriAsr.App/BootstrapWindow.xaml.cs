using System.Windows;

namespace TriAsr.App;

public partial class BootstrapWindow : Window
{
    public BootstrapWindow(BootstrapViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}