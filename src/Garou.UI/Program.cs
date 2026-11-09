using Avalonia;
using Avalonia.Rendering.Composition;
using Garou.Composition;
using Garou.Infrastructure.Persistence;
using Garou.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;
using System.Threading;

namespace Garou
{
    internal sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            System.Diagnostics.Debug.WriteLine($"Garou.UI starting with args: {string.Join(" ", args)}");

            var services = ServiceRegistration.Build();
            var settingsStore = services.GetRequiredService<SettingsStore>();
            var settingsSession = services.GetRequiredService<AppSettingsSession>();
            settingsSession.Initialize(settingsStore.LoadAsync(CancellationToken.None).GetAwaiter().GetResult());
            App.Services = services;

            var hostedServices = services.GetServices<IHostedService>().ToList();
            using var lifetime = new CancellationTokenSource();
            App.StartupTask = Task.Run(async () =>
            {
                await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(lifetime.Token);

                foreach (var hostedService in hostedServices)
                {
                    System.Diagnostics.Debug.WriteLine($"Garou.UI starting hosted service: {hostedService.GetType().Name}");
                    await hostedService.StartAsync(lifetime.Token);
                }
            }, lifetime.Token);

            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                lifetime.Cancel();
                try
                {
                    App.StartupTask.GetAwaiter().GetResult();
                }
                finally
                {
                    foreach (var hostedService in hostedServices)
                    {
                        hostedService.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                    }

                    settingsStore.SaveAsync(settingsSession.Current, CancellationToken.None).GetAwaiter().GetResult();
                }
            }
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new SkiaOptions
                {
                    MaxGpuResourceSizeBytes = 256 * 1024 * 1024 // Set cache to 256MB. Without this, there are issues with large images.
                })
                .With(new CompositionOptions
                {
                    UseRegionDirtyRectClipping = true
                })
                .WithInterFont()
                .LogToTrace();
    }
}
