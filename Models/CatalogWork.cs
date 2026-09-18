using System.ComponentModel.DataAnnotations;

namespace KategoriSecici.Models;

// Shared work metadata is independent of each user's personal list.
public class CatalogWork
{
    public int Id { get; set; }
    public MedyaKategori Kategori { get; set; }

    [Required, StringLength(200)]
    public string Ad { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string NormalizedTitle { get; set; } = string.Empty;

    public int? ReleaseYear { get; set; }

    [StringLength(200)]
    public string AltName { get; set; } = string.Empty;

    [StringLength(600)]
    public string PosterUrl { get; set; } = string.Empty;

    [StringLength(240)]
    public string Tur { get; set; } = string.Empty;

    [StringLength(3000)]
    public string Konu { get; set; } = string.Empty;

    [StringLength(80)]
    public string Puan { get; set; } = string.Empty;

    [StringLength(80)]
    public string Fiyat { get; set; } = string.Empty;

    [StringLength(80)]
    public string YayinTarihi { get; set; } = string.Empty;

    [StringLength(240)]
    public string Yapimci { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<CatalogExternalId> ExternalIds { get; set; } = new();
}
