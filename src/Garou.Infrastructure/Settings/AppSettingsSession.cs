using Garou.Domain.Models;

namespace Garou.Infrastructure.Settings;

public sealed class AppSettingsSession
{
    public AppSettings Current { get; private set; } = new();

    public void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = settings;
    }
}
