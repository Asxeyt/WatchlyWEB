using System.ComponentModel.DataAnnotations;

namespace KategoriSecici.Models;

public class CatalogExternalId
{
    public int Id { get; set; }
    public int CatalogWorkId { get; set; }
    public CatalogWork CatalogWork { get; set; } = null!;
    public MedyaKategori Kategori { get; set; }

    [Required, StringLength(40)]
    public string Source { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string ExternalId { get; set; } = string.Empty;
}
