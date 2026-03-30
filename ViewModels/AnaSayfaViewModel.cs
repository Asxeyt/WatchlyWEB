using KategoriSecici.Models;

namespace KategoriSecici.ViewModels;

public class AnaSayfaViewModel
{
    public string Dil { get; set; } = "tr";

    public string? YeniOgeAdi { get; set; }

    public string? YeniOgePosterUrl { get; set; }

    public string? YeniOgeTur { get; set; }

    public string? YeniOgeKonu { get; set; }

    public string? YeniOgePuan { get; set; }

    public string? YeniOgeFiyat { get; set; }

    public MedyaKategori SeciliKategori { get; set; } = MedyaKategori.Film;

    public List<MedyaOgesi> SeciliKategoriOgeleri { get; set; } = new();

    public List<MedyaOgesi> SeciliKategoriListeOgeleri { get; set; } = new();

    public List<MedyaOgesi> SeciliKategoriIzlenenOgeleri { get; set; } = new();

    public string? RastgeleSecilenOge { get; set; }

    public MedyaOgesi? RastgeleSecilenDetay { get; set; }

    public IReadOnlyDictionary<MedyaKategori, int> KategoriAdetleri { get; set; }
        = new Dictionary<MedyaKategori, int>();
}
