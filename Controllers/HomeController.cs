using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
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
    private readonly IHttpClientFactory _httpClientFactory;

    public HomeController(ILogger<HomeController> logger, AppDbContext dbContext, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
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
    public async Task<IActionResult> SearchCatalog(MedyaKategori kategori, string q, string lang = "tr")
    {
        var currentLang = NormalizeLang(lang);
        var query = (q ?? string.Empty).Trim();
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        if (query.Length < 2)
        {
            return Json(new
            {
                items = Array.Empty<CatalogSuggestionViewModel>(),
                message = currentLang == "en" ? "Type at least 2 characters." : "En az 2 karakter yaz."
            });
        }

        var providerResults = kategori switch
        {
            MedyaKategori.Anime => await SearchJikanAsync(query, true),
            MedyaKategori.Manga => await SearchJikanAsync(query, false),
            MedyaKategori.Kitap => await SearchOpenLibraryAsync(query),
            _ => new List<CatalogSuggestionViewModel>()
        };

        var listResults = await SearchFromUserListAsync(query, kategori, userId.Value);
        var results = listResults
            .Concat(providerResults)
            .GroupBy(x => x.Name.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .Take(12)
            .ToList();

        if (results.Count == 0)
        {
            var noResult = kategori switch
            {
                MedyaKategori.Anime => currentLang == "en" ? "No anime found." : "Boyle bir anime yok.",
                MedyaKategori.Manga => currentLang == "en" ? "No manga found." : "Boyle bir manga yok.",
                MedyaKategori.Kitap => currentLang == "en" ? "No book found." : "Boyle bir kitap yok.",
                _ => currentLang == "en" ? "No result found." : "Sonuc bulunamadi."
            };

            return Json(new
            {
                items = Array.Empty<CatalogSuggestionViewModel>(),
                message = noResult
            });
        }

        return Json(new
        {
            items = results.Take(8),
            message = string.Empty
        });
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

    private async Task<List<CatalogSuggestionViewModel>> SearchJikanAsync(string query, bool anime)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);

        var endpoint = anime ? "anime" : "manga";
        var url = $"https://api.jikan.moe/v4/{endpoint}?q={Uri.EscapeDataString(query)}&limit=20&sfw=true";

        var items = await FetchJikanAsync(client, url);
        if (items.Count == 0 && query.Length >= 4)
        {
            var fallback = query[..^1];
            var fallbackUrl = $"https://api.jikan.moe/v4/{endpoint}?q={Uri.EscapeDataString(fallback)}&limit=20&sfw=true";
            items = await FetchJikanAsync(client, fallbackUrl);
        }

        return RankFuzzy(query, items);
    }

    private static async Task<List<CatalogSuggestionViewModel>> FetchJikanAsync(HttpClient client, string url)
    {
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var item in data.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                var englishTitle = item.TryGetProperty("title_english", out var te) ? (te.GetString() ?? string.Empty) : string.Empty;
                var synopsis = item.TryGetProperty("synopsis", out var s) ? (s.GetString() ?? string.Empty) : string.Empty;
                var score = item.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number
                    ? sc.GetDouble().ToString("0.0")
                    : string.Empty;

                string poster = string.Empty;
                if (item.TryGetProperty("images", out var images) &&
                    images.TryGetProperty("jpg", out var jpg) &&
                    jpg.TryGetProperty("image_url", out var imageUrl))
                {
                    poster = imageUrl.GetString() ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = englishTitle,
                    PosterUrl = poster,
                    Overview = synopsis,
                    Score = score
                });
            }

            return list;
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private static List<CatalogSuggestionViewModel> RankFuzzy(string query, List<CatalogSuggestionViewModel> source)
    {
        static string Normalize(string v)
        {
            var s = (v ?? string.Empty).Trim().ToLowerInvariant();
            return new string(s.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)).ToArray());
        }

        var q = Normalize(query);
        if (string.IsNullOrWhiteSpace(q))
        {
            return new List<CatalogSuggestionViewModel>();
        }

        return source
            .Select(x =>
            {
                var primary = Normalize(x.Name);
                var alt = Normalize(x.AltName);
                var best = Similarity(q, primary);
                if (!string.IsNullOrWhiteSpace(alt))
                {
                    best = Math.Max(best, Similarity(q, alt));
                }

                if (primary.Contains(q, StringComparison.Ordinal))
                {
                    best = Math.Max(best, 0.99);
                }

                return new { Item = x, Score = best };
            })
            .Where(x => x.Score >= 0.25)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Name)
            .Select(x => x.Item)
            .ToList();
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchFromUserListAsync(string query, MedyaKategori kategori, int userId)
    {
        var rows = await _dbContext.MedyaOgeleri
            .Where(x => x.AppUserId == userId && x.Kategori == kategori)
            .Select(x => x.Ad)
            .ToListAsync();

        var local = rows
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => new CatalogSuggestionViewModel { Name = x })
            .ToList();

        return RankFuzzy(query, local);
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchOpenLibraryAsync(string query)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = $"https://openlibrary.org/search.json?q={Uri.EscapeDataString(query)}&limit=20";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var item in docs.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var author = string.Empty;
                if (item.TryGetProperty("author_name", out var authors) &&
                    authors.ValueKind == JsonValueKind.Array &&
                    authors.GetArrayLength() > 0)
                {
                    author = authors[0].GetString() ?? string.Empty;
                }

                var year = string.Empty;
                if (item.TryGetProperty("first_publish_year", out var y) && y.ValueKind == JsonValueKind.Number)
                {
                    year = y.GetInt32().ToString();
                }

                var cover = string.Empty;
                if (item.TryGetProperty("cover_i", out var coverId) && coverId.ValueKind == JsonValueKind.Number)
                {
                    cover = $"https://covers.openlibrary.org/b/id/{coverId.GetInt32()}-M.jpg";
                }

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = author,
                    PosterUrl = cover,
                    Overview = year,
                    Score = year
                });
            }

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private static double Similarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0;
        }

        var dist = LevenshteinDistance(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        return maxLen == 0 ? 1 : 1 - (double)dist / maxLen;
    }

    private static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= m; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }
}
