using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Garou.ViewModels;
using Garou.Views;
using Garou.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace Garou
{
    public partial class App : Avalonia.Application
    {
        public static IServiceProvider Services { get; set; } = null!;
        internal static Task StartupTask { get; set; } = Task.CompletedTask;

        public override void Initialize()
        {
            System.Diagnostics.Debug.WriteLine("Garou.UI initializing Avalonia application");

            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                System.Diagnostics.Debug.WriteLine("Garou.UI initializing main window");
                ApplyTheme(Services.GetRequiredService<AppSettingsSession>().Current.ThemePreference);

                desktop.MainWindow = new MainWindow(Services.GetRequiredService<AppSettingsSession>())
                {
                    DataContext = Services.GetRequiredService<MainViewModel>(),
                };
            }

            base.OnFrameworkInitializationCompleted();
        }

        internal static void ApplyTheme(string themePreference)
        {
            if (Current is not App app)
            {
                return;
            }

            app.RequestedThemeVariant = themePreference switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
    }
}