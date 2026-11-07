using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Garou.ViewModels;
using Garou.Views;
using Garou.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Garou
{
    public partial class App : Avalonia.Application
    {
        public static IServiceProvider Services { get; set; } = null!;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow(Services.GetRequiredService<AppSettingsSession>())
                {
                    DataContext = Services.GetRequiredService<MainViewModel>(),
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}