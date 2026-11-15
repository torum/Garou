using Avalonia;
using Avalonia.Headless;
using Garou;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
