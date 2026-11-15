using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Garou.Localization;

namespace Garou.UI.Views;

internal sealed class XnfoCountryPicker : StackPanel
{
    // ISO 3166-1 alpha-2, including territories that may not have a system locale.
    private const string Codes = "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW";
    private static readonly HashSet<string> ValidCodes = new(Codes.Split(' '), StringComparer.Ordinal);
    private readonly List<string> _selected;
    private readonly TextBox _search = new();
    private readonly StackPanel _items = new();
    private readonly TextBlock _count = new();
    private readonly ResourceLocalizer _localizer;
    private readonly List<(string Code, string Name)> _countries;

    public XnfoCountryPicker(IEnumerable<string>? selected, ResourceLocalizer localizer, CultureInfo culture)
    {
        _localizer = localizer;
        _selected = (selected ?? []).Select(value => value.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        _countries = CreateCountries(culture);
        Spacing = 4;
        var label = localizer["XnfoCountries"];
        var searchLabel = localizer["XnfoCountrySearch"];
        _search.PlaceholderText = searchLabel;
        AutomationProperties.SetName(_search, searchLabel);
        _search.TextChanged += (_, _) => Refresh();
        Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        Children.Add(_search);
        Children.Add(_count);
        Children.Add(new ScrollViewer
        {
            Content = _items,
            Height = 180,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Refresh();
    }

    public bool HasInvalidSelection => _selected.Any(code => !ValidCodes.Contains(code));

    public List<string> SelectedCodes => [.. _selected];

    private static List<(string Code, string Name)> CreateCountries(CultureInfo culture)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = culture;
            return ValidCodes.Select(code => (Code: code, Name: CountryName(code)))
                .OrderBy(item => item.Name, StringComparer.Create(culture, true)).ToList();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    private static string CountryName(string code)
    {
        try
        {
            var region = new RegionInfo(code);
            return string.Join(" / ", new[] { region.DisplayName, region.EnglishName, region.NativeName }.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    private void Refresh()
    {
        _items.Children.Clear();
        _count.Text = string.Format(CultureInfo.CurrentCulture, _localizer["XnfoCountriesSelected"], _selected.Count);
        var search = (_search.Text ?? string.Empty).Trim();
        foreach (var invalid in _selected.Where(code => !ValidCodes.Contains(code)).ToArray())
        {
            AddOption(invalid, $"{invalid} — {_localizer["XnfoCountryInvalidEntry"]}");
        }
        foreach (var (code, name) in _countries)
        {
            if (search.Length == 0 || code.Contains(search, StringComparison.OrdinalIgnoreCase) || name.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                AddOption(code, $"{name} ({code})");
            }
        }
    }

    private void AddOption(string code, string label)
    {
        var checkbox = new CheckBox { Content = label, IsChecked = _selected.Contains(code, StringComparer.Ordinal) };
        checkbox.IsCheckedChanged += (_, _) =>
        {
            if (checkbox.IsChecked == true)
            {
                if (!_selected.Contains(code, StringComparer.Ordinal)) _selected.Add(code);
            }
            else
            {
                _selected.Remove(code);
            }
            _count.Text = string.Format(CultureInfo.CurrentCulture, _localizer["XnfoCountriesSelected"], _selected.Count);
            if (!ValidCodes.Contains(code)) Refresh();
        };
        _items.Children.Add(checkbox);
    }
}
