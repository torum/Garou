using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Garou.Domain.Models;
using Garou.Localization;

namespace Garou.UI.Views;

/// <summary>A standalone, in-memory editor for user-authored XNFO metadata.</summary>
public sealed class XnfoEditorDialog : Window
{
    private readonly string _mediaPath;
    private readonly ResourceLocalizer _localizer;
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBox _title = new();
    private readonly TextBox _subtitle = new();
    private readonly TextBox _sortTitle = new();
    private readonly TextBox _publisher = new();
    private readonly TextBox _studioLabel = new();
    private readonly TextBox _releaseDate = new();
    private readonly TextBox _contentRating = new();
    private readonly TextBox _description = new();
    private readonly TextBox _comments = new();
    private readonly TextBox _coverArt = new();
    private readonly TextBox _performers = new();
    private readonly XnfoCountryPicker _countries;
    private readonly TextBox _tags = new();
    private readonly TextBox _genres = new();
    private readonly TextBox _categories = new();

    /// <summary>
    /// Shows the editor and returns a new metadata instance on confirmation, or null on cancel.
    /// Relative cover paths are interpreted relative to the source media file's directory.
    /// This method does not modify the supplied instance or write any files.
    /// </summary>
    public static Task<XnfoMetadata?> ShowAsync(Window owner, string mediaPath, XnfoMetadata metadata, string language)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentNullException.ThrowIfNull(metadata);
        return new XnfoEditorDialog(mediaPath, metadata, language).ShowDialog<XnfoMetadata?>(owner);
    }

    private XnfoEditorDialog(string mediaPath, XnfoMetadata metadata, string language)
    {
        _mediaPath = mediaPath;
        var culture = CultureInfo.GetCultureInfo(language?.StartsWith("ja", StringComparison.OrdinalIgnoreCase) == true ? "ja" : "en");
        _localizer = new ResourceLocalizer(culture);
        _countries = new XnfoCountryPicker(metadata.Countries, _localizer, culture);
        Title = _localizer["XnfoEditorTitle"];
        Width = 650;
        Height = 750;
        MinWidth = 400;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var fields = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        AddField(fields, _title, _localizer["XnfoTitle"], metadata.Title);
        AddField(fields, _subtitle, _localizer["XnfoSubtitle"], metadata.Subtitle);
        AddField(fields, _sortTitle, _localizer["XnfoSortTitle"], metadata.SortTitle);
        AddField(fields, _publisher, _localizer["XnfoPublisher"], metadata.Publisher);
        AddField(fields, _studioLabel, _localizer["XnfoStudioLabel"], metadata.StudioLabel);
        AddField(fields, _releaseDate, _localizer["XnfoReleaseDate"], metadata.ReleaseDate);
        AddField(fields, _contentRating, _localizer["XnfoContentRating"], metadata.ContentRating);
        AddField(fields, _description, _localizer["XnfoDescription"], metadata.Description, multiline: true);
        AddField(fields, _comments, _localizer["XnfoComments"], metadata.Comments, multiline: true);
        AddCoverField(fields, metadata.CoverArt);
        AddField(fields, _performers, _localizer["XnfoPerformers"], JoinLines(metadata.Performers), multiline: true);
        fields.Children.Add(_countries);
        AddField(fields, _tags, _localizer["XnfoTags"], JoinLines(metadata.Tags), multiline: true);
        AddField(fields, _genres, _localizer["XnfoGenres"], JoinLines(metadata.Genres), multiline: true);
        AddField(fields, _categories, _localizer["XnfoCategories"], JoinLines(metadata.Categories), multiline: true);

        var save = new Button { Content = _localizer["XnfoConfirm"] };
        var cancel = new Button { Content = _localizer["CancelLabel"] };
        save.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { save, cancel } };
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 16), Children = { _error, buttons } };
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);
        Content = layout;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private static void AddField(Panel parent, TextBox input, string label, string? value, bool multiline = false)
    {
        input.Text = value ?? string.Empty;
        AutomationProperties.SetName(input, label);
        if (multiline)
        {
            input.AcceptsReturn = true;
            input.TextWrapping = TextWrapping.Wrap;
            input.MinHeight = 72;
        }
        parent.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, input } });
    }

    private void AddCoverField(Panel parent, string? value)
    {
        var label = _localizer["XnfoCoverPath"];
        _coverArt.Text = value ?? string.Empty;
        AutomationProperties.SetName(_coverArt, label);
        var browse = new Button { Content = _localizer["XnfoBrowse"] };
        browse.Click += async (_, _) => await BrowseCoverAsync();
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(_coverArt);
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        parent.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, row } });
    }

    private async Task BrowseCoverAsync()
    {
        try
        {
            var directory = MediaDirectory();
            var current = _coverArt.Text?.Trim();
            var startDirectory = string.IsNullOrEmpty(current) ? directory : Path.GetDirectoryName(Path.GetFullPath(current, directory)) ?? directory;
            var folder = await StorageProvider.TryGetFolderFromPathAsync(startDirectory);
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = _localizer["XnfoChooseCover"],
                AllowMultiple = false,
                SuggestedStartLocation = folder,
                FileTypeFilter = [new FilePickerFileType(_localizer["XnfoImages"]) { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.bmp"] }]
            });
            if (files.Count == 0)
            {
                return;
            }
            var path = files[0].TryGetLocalPath();
            if (path is null)
            {
                ShowError(_localizer["XnfoCoverLocalRequired"]);
                return;
            }
            _coverArt.Text = Path.GetRelativePath(directory, path);
            _error.IsVisible = false;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            ShowError($"{_localizer["XnfoCoverBrowseError"]}: {exception.Message}");
        }
    }

    private string MediaDirectory() => Path.GetDirectoryName(Path.GetFullPath(_mediaPath))
        ?? throw new ArgumentException("The media path has no parent directory.");

    private void Confirm()
    {
        var date = (_releaseDate.Text ?? string.Empty).Trim();
        if (!IsValidDate(date))
        {
            ShowError(_localizer["XnfoInvalidReleaseDate"]);
            _releaseDate.Focus();
            return;
        }
        if (_countries.HasInvalidSelection)
        {
            ShowError(_localizer["XnfoInvalidCountries"]);
            return;
        }
        var cover = (_coverArt.Text ?? string.Empty).Trim();
        if (cover.Length > 0)
        {
            try
            {
                _ = Path.GetFullPath(cover, MediaDirectory());
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                ShowError(_localizer["XnfoInvalidCoverPath"]);
                _coverArt.Focus();
                return;
            }
        }
        Close(new XnfoMetadata
        {
            Title = _title.Text ?? string.Empty,
            Subtitle = _subtitle.Text ?? string.Empty,
            SortTitle = _sortTitle.Text ?? string.Empty,
            Publisher = _publisher.Text ?? string.Empty,
            StudioLabel = _studioLabel.Text ?? string.Empty,
            ReleaseDate = date,
            ContentRating = _contentRating.Text ?? string.Empty,
            Description = _description.Text ?? string.Empty,
            Comments = _comments.Text ?? string.Empty,
            CoverArt = cover,
            Performers = SplitLines(_performers.Text),
            Countries = _countries.SelectedCodes,
            Tags = SplitLines(_tags.Text),
            Genres = SplitLines(_genres.Text),
            Categories = SplitLines(_categories.Text)
        });
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private static string JoinLines(IEnumerable<string>? values) => string.Join(Environment.NewLine, values ?? []);

    private static List<string> SplitLines(string? text) => (text ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(value => value.Length > 0).ToList();

    private static bool IsValidDate(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }
        if (value.Length is not (4 or 7 or 10) ||
            (value.Length >= 7 && value[4] != '-') || (value.Length == 10 && value[7] != '-'))
        {
            return false;
        }
        for (var i = 0; i < value.Length; i++)
        {
            if (i is 4 or 7 && value[i] == '-') continue;
            if (value[i] is < '0' or > '9') return false;
        }
        var year = int.Parse(value.AsSpan(0, 4), CultureInfo.InvariantCulture);
        if (year == 0) return false;
        if (value.Length == 4) return true;
        var month = int.Parse(value.AsSpan(5, 2), CultureInfo.InvariantCulture);
        if (month is < 1 or > 12) return false;
        if (value.Length == 7) return true;
        var day = int.Parse(value.AsSpan(8, 2), CultureInfo.InvariantCulture);
        return day >= 1 && day <= DateTime.DaysInMonth(year, month);
    }
}
