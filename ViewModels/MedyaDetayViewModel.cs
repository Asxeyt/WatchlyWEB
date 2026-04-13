using KategoriSecici.Models;

namespace KategoriSecici.ViewModels;

public class MedyaDetayViewModel
{
    public string Dil { get; set; } = "tr";

    public MedyaKategori Kategori { get; set; } = MedyaKategori.Film;

    public int? KayitId { get; set; }

    public bool ListedeMi { get; set; }

    public bool TamamlandiMi { get; set; }

    public string Ad { get; set; } = string.Empty;

    public string PosterUrl { get; set; } = string.Empty;

    public string Tur { get; set; } = string.Empty;

    public string Konu { get; set; } = string.Empty;

    public string Puan { get; set; } = string.Empty;

    public string Fiyat { get; set; } = string.Empty;

    public string YayinTarihi { get; set; } = string.Empty;

    public string Oyuncular { get; set; } = string.Empty;

    public string Yapimci { get; set; } = string.Empty;

    public string FragmanUrl { get; set; } = string.Empty;

    public string FragmanEmbedUrl { get; set; } = string.Empty;

    public bool FragmanDogrudanVideo { get; set; }

    public List<string> Gorseller { get; set; } = new();

    public List<MedyaKisiKartViewModel> OyuncuKartlari { get; set; } = new();

    public List<CatalogSuggestionViewModel> BenzerIcerikler { get; set; } = new();
}
