using Avalonia.Controls;
using Avalonia.Interactivity;
using Garou.ViewModels;

namespace Garou.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    public SettingsWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.Settings.LoadAsync(CancellationToken.None);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
