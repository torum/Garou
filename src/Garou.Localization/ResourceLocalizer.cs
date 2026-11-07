using System.Globalization;
using System.Resources;
using Garou.Domain.Abstractions;

namespace Garou.Localization;

public sealed class ResourceLocalizer : ILocalizer
{
    private readonly ResourceManager _resourceManager = new("Garou.Localization.Strings", typeof(ResourceLocalizer).Assembly);
    private readonly CultureInfo _culture;

    public ResourceLocalizer(CultureInfo culture)
    {
        _culture = culture;
    }

    public string this[string key] => _resourceManager.GetString(key, _culture) ?? key;
}
