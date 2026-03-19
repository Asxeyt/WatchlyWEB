using KategoriSecici.Models;

namespace KategoriSecici.Data;

public static class SeedData
{
    public static void Initialize(AppDbContext context)
    {
        if (context.MedyaOgeleri.Any())
        {
            return;
        }

        var baslangicListesi = new List<MedyaOgesi>
        {
            new() { Ad = "Spirited Away", Kategori = MedyaKategori.Film },
            new() { Ad = "Interstellar", Kategori = MedyaKategori.Film },
            new() { Ad = "Attack on Titan", Kategori = MedyaKategori.Anime },
            new() { Ad = "Death Note", Kategori = MedyaKategori.Anime },
            new() { Ad = "Berserk", Kategori = MedyaKategori.Manga },
            new() { Ad = "1984", Kategori = MedyaKategori.Kitap },
            new() { Ad = "Dark", Kategori = MedyaKategori.Dizi },
            new() { Ad = "The Witcher 3", Kategori = MedyaKategori.Oyun }
        };

        context.MedyaOgeleri.AddRange(baslangicListesi);
        context.SaveChanges();
    }
}
