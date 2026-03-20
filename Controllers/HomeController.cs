using System.Diagnostics;
using System.Security.Claims;
using KategoriSecici.Data;
using KategoriSecici.Models;
using KategoriSecici.ViewModels;
using Microsoft.AspNetCore.Authorization;
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
    public IActionResult Anasayfa(string lang = "tr")
    {
        var currentLang = NormalizeLang(lang);
        ViewData["Lang"] = currentLang;
        ViewData["SelectedCategory"] = MedyaKategori.Film.ToString();
        ViewData["BodyClass"] = "anasayfa-page";
        return View();
    }

    [HttpGet]
    public IActionResult Landing(string lang = "tr")
    {
        return RedirectToAction(nameof(Anasayfa), new { lang = NormalizeLang(lang) });
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Index(string lang = "tr", MedyaKategori kategori = MedyaKategori.Film)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return RedirectToAction("Login", "Account", new { lang = NormalizeLang(lang) });
        }

        await ClaimLegacyUnownedItemsAsync(userId.Value);

        var currentLang = NormalizeLang(lang);
        ViewData["Lang"] = currentLang;
        ViewData["SelectedCategory"] = kategori.ToString();
        ViewData["BodyClass"] = "index-page";
        var model = await BuildViewModelAsync(currentLang, kategori, userId.Value);
        return View(model);
    }

    [HttpGet]
    [Authorize]
    public IActionResult RastgeleKategori(string lang = "tr")
    {
        var currentLang = NormalizeLang(lang);
        var kategoriler = Enum.GetValues<MedyaKategori>();
        var secim = kategoriler[Random.Shared.Next(kategoriler.Length)];
        return RedirectToAction(nameof(Index), new { lang = currentLang, kategori = secim });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> Ekle(AnaSayfaViewModel form)
    {
        var lang = NormalizeLang(form.Dil);
        var seciliKategori = form.SeciliKategori;
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return RedirectToAction("Login", "Account", new { lang });
        }

        if (string.IsNullOrWhiteSpace(form.YeniOgeAdi))
        {
            TempData["Mesaj"] = lang == "en" ? "Name cannot be empty." : "Isim bos olamaz.";
            TempData["MesajTipi"] = "warning";
            return RedirectToAction(nameof(Index), new { lang, kategori = seciliKategori });
        }

        var yeniKayit = new MedyaOgesi
        {
            Ad = form.YeniOgeAdi.Trim(),
            Kategori = seciliKategori,
            AppUserId = userId.Value,
            Izlendi = false
        };

        _dbContext.MedyaOgeleri.Add(yeniKayit);
        await _dbContext.SaveChangesAsync();

        TempData["Mesaj"] = lang == "en" ? "Item added." : "Oge eklendi.";
        TempData["MesajTipi"] = "success";

        return RedirectToAction(nameof(Index), new { lang, kategori = seciliKategori });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> Sil(int id, MedyaKategori kategori, string dil = "tr")
    {
        var lang = NormalizeLang(dil);
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return RedirectToAction("Login", "Account", new { lang });
        }

        var kayit = await _dbContext.MedyaOgeleri.FirstOrDefaultAsync(x => x.Id == id && x.AppUserId == userId.Value);
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> IzledimYap(int id, MedyaKategori kategori, string dil = "tr")
    {
        var lang = NormalizeLang(dil);
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return RedirectToAction("Login", "Account", new { lang });
        }

        var kayit = await _dbContext.MedyaOgeleri.FirstOrDefaultAsync(x => x.Id == id && x.AppUserId == userId.Value);
        if (kayit is null)
        {
            TempData["Mesaj"] = lang == "en" ? "Item not found." : "Kayit bulunamadi.";
            TempData["MesajTipi"] = "warning";
            return RedirectToAction(nameof(Index), new { lang, kategori });
        }

        kayit.Izlendi = true;
        await _dbContext.SaveChangesAsync();
        return RedirectToAction(nameof(Index), new { lang, kategori });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> ListeyeGeriAl(int id, MedyaKategori kategori, string dil = "tr")
    {
        var lang = NormalizeLang(dil);
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return RedirectToAction("Login", "Account", new { lang });
        }

        var kayit = await _dbContext.MedyaOgeleri.FirstOrDefaultAsync(x => x.Id == id && x.AppUserId == userId.Value);
        if (kayit is null)
        {
            TempData["Mesaj"] = lang == "en" ? "Item not found." : "Kayit bulunamadi.";
            TempData["MesajTipi"] = "warning";
            return RedirectToAction(nameof(Index), new { lang, kategori });
        }

        kayit.Izlendi = false;
        await _dbContext.SaveChangesAsync();
        return RedirectToAction(nameof(Index), new { lang, kategori });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> RastgeleSec(MedyaKategori kategori, string dil = "tr")
    {
        var lang = NormalizeLang(dil);
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return RedirectToAction("Login", "Account", new { lang });
        }

        var kayitlar = await _dbContext.MedyaOgeleri
            .Where(x => x.Kategori == kategori && !x.Izlendi && x.AppUserId == userId.Value)
            .Select(x => x.Ad)
            .ToListAsync();

        if (kayitlar.Count == 0)
        {
            TempData["Mesaj"] = lang == "en" ? "No items in this category yet." : "Bu kategoride henuz oge yok.";
            TempData["MesajTipi"] = "warning";
            return RedirectToAction(nameof(Index), new { lang, kategori });
        }

        TempData["RastgeleSonuc"] = kayitlar[Random.Shared.Next(kayitlar.Count)];
        return RedirectToAction(nameof(Index), new { lang, kategori });
    }

    public IActionResult Privacy(string lang = "tr")
    {
        ViewData["Lang"] = NormalizeLang(lang);
        return View();
    }

    [HttpGet]
    [Authorize]
    public IActionResult Settings(string lang = "tr")
    {
        var currentLang = NormalizeLang(lang);
        ViewData["Lang"] = currentLang;
        ViewData["SelectedCategory"] = MedyaKategori.Film.ToString();
        ViewData["BodyClass"] = "settings-page";
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private async Task<AnaSayfaViewModel> BuildViewModelAsync(string? lang, MedyaKategori seciliKategori, int userId)
    {
        var currentLang = NormalizeLang(lang);

        var seciliListe = await _dbContext.MedyaOgeleri
            .Where(x => x.Kategori == seciliKategori && x.AppUserId == userId)
            .OrderBy(x => x.Ad)
            .ToListAsync();

        var adetler = await _dbContext.MedyaOgeleri
            .Where(x => x.AppUserId == userId)
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
            SeciliKategoriListeOgeleri = seciliListe.Where(x => !x.Izlendi).ToList(),
            SeciliKategoriIzlenenOgeleri = seciliListe.Where(x => x.Izlendi).ToList(),
            RastgeleSecilenOge = TempData["RastgeleSonuc"] as string,
            KategoriAdetleri = kategoriAdetleri
        };
    }

    private static string NormalizeLang(string? lang)
    {
        return string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";
    }

    private int? GetCurrentUserId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idValue, out var id) ? id : null;
    }

    private async Task ClaimLegacyUnownedItemsAsync(int userId)
    {
        var userOwnedCount = await _dbContext.MedyaOgeleri.CountAsync(x => x.AppUserId == userId);
        if (userOwnedCount > 0)
        {
            return;
        }

        var legacyRows = await _dbContext.MedyaOgeleri.Where(x => x.AppUserId == null).ToListAsync();
        if (legacyRows.Count == 0)
        {
            return;
        }

        foreach (var row in legacyRows)
        {
            row.AppUserId = userId;
        }

        await _dbContext.SaveChangesAsync();
    }
}
