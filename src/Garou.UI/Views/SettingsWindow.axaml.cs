using Avalonia.Controls;
using Garou.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Garou.UI.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        DataContext = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }
}
