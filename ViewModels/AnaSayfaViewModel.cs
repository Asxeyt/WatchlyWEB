using KategoriSecici.Models;

namespace KategoriSecici.ViewModels;

public class AnaSayfaViewModel
{
    public string Dil { get; set; } = "tr";

    public string? YeniOgeAdi { get; set; }

    public MedyaKategori SeciliKategori { get; set; } = MedyaKategori.Film;

    public List<MedyaOgesi> SeciliKategoriOgeleri { get; set; } = new();

    public IReadOnlyDictionary<MedyaKategori, int> KategoriAdetleri { get; set; }
        = new Dictionary<MedyaKategori, int>();
}
