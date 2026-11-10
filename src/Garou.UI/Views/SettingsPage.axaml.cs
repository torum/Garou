using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Garou.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Garou.UI.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        DataContext = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.Settings.LoadAsync(CancellationToken.None);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        var frame = this.GetVisualAncestors().OfType<FAFrame>().FirstOrDefault();
        if (frame is not null)
        {
            frame.Navigate(typeof(MediaListBoxPage));
            return;
        }

        this.GetVisualAncestors().OfType<Window>().FirstOrDefault()?.Close();
    }
}
