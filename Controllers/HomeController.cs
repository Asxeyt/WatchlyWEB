using System.Diagnostics;
using KategoriSecici.Data;
using KategoriSecici.Models;
using KategoriSecici.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KategoriSecici.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly AppDbContext _dbContext;

    public HomeController(ILogger<HomeController> logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string lang = "tr", MedyaKategori kategori = MedyaKategori.Film)
    {
        var currentLang = NormalizeLang(lang);
        ViewData["Lang"] = currentLang;
        ViewData["SelectedCategory"] = kategori.ToString();
        var model = await BuildViewModelAsync(currentLang, kategori);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ekle(AnaSayfaViewModel form)
    {
        var lang = NormalizeLang(form.Dil);
        var seciliKategori = form.SeciliKategori;

        if (string.IsNullOrWhiteSpace(form.YeniOgeAdi))
        {
            TempData["Mesaj"] = lang == "en" ? "Name cannot be empty." : "Isim bos olamaz.";
            TempData["MesajTipi"] = "warning";
            return RedirectToAction(nameof(Index), new { lang, kategori = seciliKategori });
        }

        var yeniKayit = new MedyaOgesi
        {
            Ad = form.YeniOgeAdi.Trim(),
            Kategori = seciliKategori
        };

        _dbContext.MedyaOgeleri.Add(yeniKayit);
        await _dbContext.SaveChangesAsync();

        TempData["Mesaj"] = lang == "en" ? "Item added." : "Oge eklendi.";
        TempData["MesajTipi"] = "success";

        return RedirectToAction(nameof(Index), new { lang, kategori = seciliKategori });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sil(int id, MedyaKategori kategori, string dil = "tr")
    {
        var lang = NormalizeLang(dil);

        var kayit = await _dbContext.MedyaOgeleri.FirstOrDefaultAsync(x => x.Id == id);
        if (kayit is null)
        {
            TempData["Mesaj"] = lang == "en" ? "Item not found." : "Kayit bulunamadi.";
            TempData["MesajTipi"] = "warning";
            return RedirectToAction(nameof(Index), new { lang, kategori });
        }

        _dbContext.MedyaOgeleri.Remove(kayit);
        await _dbContext.SaveChangesAsync();

        TempData["Mesaj"] = lang == "en" ? "Item removed." : "Oge listeden cikarildi.";
        TempData["MesajTipi"] = "success";
        return RedirectToAction(nameof(Index), new { lang, kategori });
    }

    public IActionResult Privacy(string lang = "tr")
    {
        ViewData["Lang"] = NormalizeLang(lang);
        return View();
    }

    [HttpGet]
    public IActionResult Settings(string lang = "tr")
    {
        ViewData["Lang"] = NormalizeLang(lang);
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private async Task<AnaSayfaViewModel> BuildViewModelAsync(string? lang, MedyaKategori seciliKategori)
    {
        var currentLang = NormalizeLang(lang);

        var seciliListe = await _dbContext.MedyaOgeleri
            .Where(x => x.Kategori == seciliKategori)
            .OrderBy(x => x.Ad)
            .ToListAsync();

        var adetler = await _dbContext.MedyaOgeleri
            .GroupBy(x => x.Kategori)
            .Select(g => new { Kategori = g.Key, Adet = g.Count() })
            .ToListAsync();

        var kategoriAdetleri = Enum.GetValues<MedyaKategori>()
            .ToDictionary(k => k, k => 0);

        foreach (var item in adetler)
        {
            kategoriAdetleri[item.Kategori] = item.Adet;
        }

        return new AnaSayfaViewModel
        {
            Dil = currentLang,
            SeciliKategori = seciliKategori,
            SeciliKategoriOgeleri = seciliListe,
            KategoriAdetleri = kategoriAdetleri
        };
    }

    private static string NormalizeLang(string? lang)
    {
        return string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";
    }
}
