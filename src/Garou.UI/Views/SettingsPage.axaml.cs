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
            await viewModel.LoadExcludedFoldersAsync(CancellationToken.None);
        }
    }

    private async void OnRestoreFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string path } && DataContext is MainViewModel viewModel)
        {
            await viewModel.RestoreExcludedFolderAsync(path, CancellationToken.None);
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
