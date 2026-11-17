using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Garou.Application.Queries;
using Garou.Infrastructure.Settings;
using Garou.UI.Views;
using Garou.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Garou.Tests.Queries;

[Collection("Avalonia UI")]
public sealed class SearchFieldSelectorTests
{
    [AvaloniaFact]
    public async Task SelectorDefaultsToAllAndRetainsSelectionWhenLanguageChanges()
    {
        var previousServices = App.Services;
        var session = new AppSettingsSession();
        var settings = new SettingsViewModel(session);
        var viewModel = new MainViewModel(null!, null!, null!, settings, session, null!, null!, null!, null!, null!, null!);
        using var services = new ServiceCollection()
            .AddSingleton(session)
            .AddSingleton(viewModel)
            .BuildServiceProvider();
        App.Services = services;
        var window = new Window();
        try
        {
            var page = new MediaListBoxPage();
            window.Content = page;
            window.Width = 900;
            window.Height = 600;
            window.Show();
            window.UpdateLayout();
            var selector = Assert.IsType<ComboBox>(page.FindControl<ComboBox>("SearchFieldSelector"));
            Assert.Equal(MediaSearchField.All, Assert.IsType<SearchFieldOption>(selector.SelectedItem).Field);
            var title = settings.SearchFieldOptions.Single(option => option.Field == MediaSearchField.Title);
            selector.SelectedItem = title;
            Assert.Same(title, viewModel.SelectedSearchField);
            settings.SelectedLanguage = "ja";
            Assert.Same(title, selector.SelectedItem);
            Assert.Equal("タイトル", title.Label);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            App.Services = previousServices;
        }
    }
}
