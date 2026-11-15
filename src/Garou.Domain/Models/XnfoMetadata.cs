namespace Garou.Domain.Models;

public sealed class XnfoMetadata
{
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string SortTitle { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string StudioLabel { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
    public string ContentRating { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string CoverArt { get; set; } = string.Empty;

    public List<string> Performers { get; set; } = [];
    public List<string> Countries { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> Genres { get; set; } = [];
    public List<string> Categories { get; set; } = [];
}
