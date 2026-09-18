using System.Text.Json.Serialization;

namespace KategoriSecici.ViewModels;

public class CatalogSuggestionViewModel
{
    public int? CatalogId { get; set; }

    [JsonIgnore]
    public Dictionary<string, string> ExternalIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; set; } = string.Empty;

    public string AltName { get; set; } = string.Empty;

    public string Genre { get; set; } = string.Empty;

    public string PosterUrl { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Score { get; set; } = string.Empty;

    public string Price { get; set; } = string.Empty;

    public string ReleaseDate { get; set; } = string.Empty;

    public string Cast { get; set; } = string.Empty;

    public string Creator { get; set; } = string.Empty;

    public string TrailerUrl { get; set; } = string.Empty;

    public string DetailUrl { get; set; } = string.Empty;
}
