using Avalonia.Controls;
using Avalonia.Headless;
using Garou.Application.Queries;
using Garou.Infrastructure.Settings;
using Garou.UI.Views;
using Garou.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Garou.Tests.Queries;

public sealed class SearchFieldSelectorTests
{
    [Fact]
    public async Task SelectorDefaultsToAllAndRetainsSelectionWhenLanguageChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var previousServices = App.Services;
        var session = new AppSettingsSession();
        var settings = new SettingsViewModel(session);
        var viewModel = new MainViewModel(null!, null!, null!, settings, session, null!, null!, null!, null!, null!);
        using var services = new ServiceCollection()
            .AddSingleton(session)
            .AddSingleton(viewModel)
            .BuildServiceProvider();
        App.Services = services;
        try
        {
            await using var headless = HeadlessUnitTestSession.StartNew(typeof(App));
            await headless.Dispatch(() =>
            {
                var page = new MediaListBoxPage();
                var window = new Window { Content = page, Width = 900, Height = 600 };
                try
                {
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
                }
                return true;
            }, ct);
        }
        finally
        {
            App.Services = previousServices;
        }
    }
}
