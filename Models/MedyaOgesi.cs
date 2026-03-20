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

    public int? AppUserId { get; set; }

    public bool Izlendi { get; set; } = false;

    public DateTime OlusturmaTarihi { get; set; } = DateTime.UtcNow;
}
