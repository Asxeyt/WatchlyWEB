using System.ComponentModel.DataAnnotations;

namespace KategoriSecici.Models;

public class MedyaOgesi
{
    public int Id { get; set; }

    [Required]
    [StringLength(120)]
    public string Ad { get; set; } = string.Empty;

    [Required]
    public MedyaKategori Kategori { get; set; }

    [StringLength(600)]
    public string? PosterUrl { get; set; }

    [StringLength(240)]
    public string? Tur { get; set; }

    [StringLength(3000)]
    public string? Konu { get; set; }

    [StringLength(80)]
    public string? Puan { get; set; }

    [StringLength(80)]
    public string? Fiyat { get; set; }

    public int? AppUserId { get; set; }

    // The user-list row keeps its own display data while retaining its shared-catalog identity.
    public int? CatalogWorkId { get; set; }

    public bool Izlendi { get; set; } = false;

    public int? DegerlendirmeSeviyesi { get; set; }

    public DateTime OlusturmaTarihi { get; set; } = DateTime.UtcNow;
}
