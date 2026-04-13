using System.Diagnostics;
using System.Security.Claims;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using KategoriSecici.Data;
using KategoriSecici.Models;
using KategoriSecici.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KategoriSecici.Controllers;

public class HomeController : Controller
{
    private static readonly ConcurrentDictionary<string, string> TranslationCache = new(StringComparer.Ordinal);
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

        var typedName = form.YeniOgeAdi.Trim();
        var userSelectedFromList =
            !string.IsNullOrWhiteSpace(form.YeniOgePosterUrl) ||
            !string.IsNullOrWhiteSpace(form.YeniOgeTur) ||
            !string.IsNullOrWhiteSpace(form.YeniOgeKonu) ||
            !string.IsNullOrWhiteSpace(form.YeniOgePuan) ||
            !string.IsNullOrWhiteSpace(form.YeniOgeFiyat);

        if (!userSelectedFromList)
        {
            var suggestions = await SearchByCategoryAsync(seciliKategori, typedName, lang);
            var best = suggestions.FirstOrDefault();
            if (best is null || !IsAcceptableMatch(typedName, best.Name))
            {
                TempData["Mesaj"] = lang == "en"
                    ? "No close result found. Please select from search suggestions."
                    : "Yakin sonuc bulunamadi. Lutfen arama onerilerinden sec.";
                TempData["MesajTipi"] = "warning";
                return RedirectToAction(nameof(Index), new { lang, kategori = seciliKategori });
            }

            form.YeniOgeAdi = best.Name;
            form.YeniOgePosterUrl = best.PosterUrl;
            form.YeniOgeTur = best.Genre;
            form.YeniOgeKonu = best.Summary;
            form.YeniOgePuan = best.Score;
            form.YeniOgeFiyat = best.Price;
        }

        var yeniKayit = new MedyaOgesi
        {
            Ad = form.YeniOgeAdi.Trim(),
            Kategori = seciliKategori,
            PosterUrl = NormalizePosterUrl(CleanValue(form.YeniOgePosterUrl, 600)),
            Tur = CleanValue(form.YeniOgeTur, 240),
            Konu = CleanValue(form.YeniOgeKonu, 3000),
            Puan = CleanValue(form.YeniOgePuan, 80),
            Fiyat = CleanValue(form.YeniOgeFiyat, 80),
            AppUserId = userId.Value,
            Izlendi = false
        };

        await FillMissingMetadataAsync(yeniKayit, lang);

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
            .ToListAsync();

        if (kayitlar.Count == 0)
        {
            TempData["Mesaj"] = lang == "en" ? "No items in this category yet." : "Bu kategoride henuz oge yok.";
            TempData["MesajTipi"] = "warning";
            return RedirectToAction(nameof(Index), new { lang, kategori });
        }

        var secilen = kayitlar[Random.Shared.Next(kayitlar.Count)];
        TempData["RastgeleSonuc"] = secilen.Ad;
        TempData["RastgeleSonucId"] = secilen.Id;
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

        if (query.Length < 1)
        {
            return Json(new
            {
                items = Array.Empty<CatalogSuggestionViewModel>(),
                message = currentLang == "en" ? "Type at least 1 character." : "En az 1 karakter yaz."
            });
        }

        var providerResults = await SearchByCategoryAsync(kategori, query, currentLang);

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
                MedyaKategori.Dizi => currentLang == "en" ? "No series found." : "Boyle bir dizi yok.",
                MedyaKategori.Film => currentLang == "en" ? "No movie found." : "Boyle bir film yok.",
                MedyaKategori.Oyun => currentLang == "en" ? "No game found." : "Boyle bir oyun yok.",
                MedyaKategori.CizgiFilm => currentLang == "en" ? "No cartoon found." : "Boyle bir cizgi film yok.",
                MedyaKategori.CizgiRoman => currentLang == "en" ? "No comic found." : "Boyle bir cizgi roman yok.",
                MedyaKategori.Webtoon => currentLang == "en" ? "No webtoon found." : "Boyle bir webtoon yok.",
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

        await EnrichMissingMetadataInCategoryAsync(seciliKategori, userId, currentLang);

        var seciliListe = await _dbContext.MedyaOgeleri
            .AsNoTracking()
            .Where(x => x.Kategori == seciliKategori && x.AppUserId == userId)
            .OrderBy(x => x.Ad)
            .ToListAsync();

        foreach (var item in seciliListe)
        {
            item.Tur = await TranslateIfNeededAsync(item.Tur, currentLang);
            item.Konu = await TranslateIfNeededAsync(item.Konu, currentLang);
        }

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
            RastgeleSecilenDetay = FindRandomDetailFromTempData(seciliListe),
            KategoriAdetleri = kategoriAdetleri
        };
    }

    private static string NormalizeLang(string? lang)
    {
        return string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";
    }

    private static string? CleanValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? NormalizePosterUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        if (v.StartsWith("//"))
        {
            return $"https:{v}";
        }

        return v;
    }

    private MedyaOgesi? FindRandomDetailFromTempData(List<MedyaOgesi> seciliListe)
    {
        var idRaw = TempData["RastgeleSonucId"]?.ToString();
        if (!int.TryParse(idRaw, out var randomId))
        {
            return null;
        }

        return seciliListe.FirstOrDefault(x => x.Id == randomId);
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

    private async Task<List<CatalogSuggestionViewModel>> SearchByCategoryAsync(MedyaKategori kategori, string query, string lang)
    {
        var all = new List<CatalogSuggestionViewModel>();
        var variants = BuildQueryVariants(query).Take(query.Trim().Length <= 2 ? 1 : 2);
        foreach (var q in variants)
        {
            List<CatalogSuggestionViewModel> part = kategori switch
            {
                MedyaKategori.Anime => await SearchJikanAsync(q, true),
                MedyaKategori.Manga => await SearchJikanAsync(q, false),
                MedyaKategori.Kitap => await SearchBooksAsync(q, comicsOnly: false, lang),
                MedyaKategori.Dizi => await SearchTvMazeAsync(q),
                MedyaKategori.Film => await SearchMoviesAsync(q),
                MedyaKategori.Oyun => await SearchSteamAsync(q, lang),
                MedyaKategori.CizgiFilm => await SearchCartoonsAsync(q, lang),
                MedyaKategori.CizgiRoman => await SearchComicsAsync(q, lang),
                MedyaKategori.Webtoon => await SearchWebtoonAsync(q, lang),
                _ => new List<CatalogSuggestionViewModel>()
            };

            all.AddRange(part);
            if (all.Count >= 60)
            {
                break;
            }
        }

        var ranked = RankFuzzy(query, all)
            .GroupBy(x => x.Name.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
        return await LocalizeCatalogItemsAsync(ranked, lang);
    }

    private static IEnumerable<string> BuildQueryVariants(string query)
    {
        var q = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(q))
        {
            return Array.Empty<string>();
        }

        var set = new List<string> { q };
        if (q.Length > 3)
        {
            set.Add(q[..^1]);
        }

        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length >= 2)
        {
            set.Add(string.Join(' ', words.Take(1)));
            set.Add(string.Join(' ', words.Take(2)));
        }

        return set
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private bool NeedsMetadata(MedyaOgesi row)
    {
        if (string.IsNullOrWhiteSpace(row.PosterUrl) ||
            string.IsNullOrWhiteSpace(row.Tur) ||
            string.IsNullOrWhiteSpace(row.Konu))
        {
            return true;
        }

        if (row.Kategori == MedyaKategori.Oyun)
        {
            return string.IsNullOrWhiteSpace(row.Fiyat);
        }

        if (row.Kategori == MedyaKategori.Kitap || row.Kategori == MedyaKategori.CizgiRoman || row.Kategori == MedyaKategori.Webtoon)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(row.Puan);
    }

    private async Task EnrichMissingMetadataInCategoryAsync(MedyaKategori kategori, int userId, string lang)
    {
        var rows = await _dbContext.MedyaOgeleri
            .Where(x => x.AppUserId == userId && x.Kategori == kategori)
            .OrderByDescending(x => x.OlusturmaTarihi)
            .Take(40)
            .ToListAsync();

        var targets = rows.Where(NeedsMetadata).Take(16).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var item in targets)
        {
            changed |= await FillMissingMetadataAsync(item, lang);
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync();
        }
    }

    private async Task<bool> FillMissingMetadataAsync(MedyaOgesi item, string lang)
    {
        var suggestions = item.Kategori switch
        {
            MedyaKategori.Anime => await SearchJikanAsync(item.Ad, true),
            MedyaKategori.Manga => await SearchJikanAsync(item.Ad, false),
            MedyaKategori.Kitap => await SearchBooksAsync(item.Ad, comicsOnly: false, lang),
            MedyaKategori.Dizi => await SearchTvMazeAsync(item.Ad),
            MedyaKategori.Film => await SearchMoviesAsync(item.Ad),
            MedyaKategori.Oyun => await SearchSteamAsync(item.Ad, lang),
            MedyaKategori.CizgiFilm => await SearchCartoonsAsync(item.Ad, lang),
            MedyaKategori.CizgiRoman => await SearchComicsAsync(item.Ad, lang),
            MedyaKategori.Webtoon => await SearchWebtoonAsync(item.Ad, lang),
            _ => new List<CatalogSuggestionViewModel>()
        };

        var best = suggestions.FirstOrDefault();
        if (best is null)
        {
            return false;
        }

        var changed = false;
        if (string.IsNullOrWhiteSpace(item.PosterUrl) && !string.IsNullOrWhiteSpace(best.PosterUrl))
        {
            item.PosterUrl = NormalizePosterUrl(CleanValue(best.PosterUrl, 600));
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(item.Tur) && !string.IsNullOrWhiteSpace(best.Genre))
        {
            item.Tur = CleanValue(await TranslateIfNeededAsync(best.Genre, lang), 240);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(item.Konu) && !string.IsNullOrWhiteSpace(best.Summary))
        {
            item.Konu = CleanValue(await TranslateIfNeededAsync(best.Summary, lang), 3000);
            changed = true;
        }

        if (item.Kategori == MedyaKategori.Oyun)
        {
            if (string.IsNullOrWhiteSpace(item.Fiyat) && !string.IsNullOrWhiteSpace(best.Price))
            {
                item.Fiyat = CleanValue(best.Price, 80);
                changed = true;
            }
        }
        else if (item.Kategori != MedyaKategori.Kitap && item.Kategori != MedyaKategori.CizgiRoman && item.Kategori != MedyaKategori.Webtoon)
        {
            if (string.IsNullOrWhiteSpace(item.Puan) && !string.IsNullOrWhiteSpace(best.Score))
            {
                item.Puan = CleanValue(best.Score, 80);
                changed = true;
            }
        }

        return changed;
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
                var genres = string.Empty;
                if (item.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array)
                {
                    genres = string.Join(", ",
                        gs.EnumerateArray()
                            .Take(3)
                            .Select(x => x.TryGetProperty("name", out var gn) ? gn.GetString() : null)
                            .Where(x => !string.IsNullOrWhiteSpace(x)));
                }

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
                    Genre = genres,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty,
                    Summary = synopsis,
                    Score = score,
                    Price = string.Empty
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
                else
                {
                    var qWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (qWords.Length > 0 && qWords.All(w => primary.Contains(w, StringComparison.Ordinal)))
                    {
                        best = Math.Max(best, 0.92);
                    }
                    if (qWords.Length > 0 && qWords.Any(w => primary.StartsWith(w, StringComparison.Ordinal)))
                    {
                        best = Math.Max(best, 0.65);
                    }
                }

                return new { Item = x, Score = best };
            })
            .Where(x => x.Score >= 0.10)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Name)
            .Select(x => x.Item)
            .ToList();
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchFromUserListAsync(string query, MedyaKategori kategori, int userId)
    {
        var rows = await _dbContext.MedyaOgeleri
            .Where(x => x.AppUserId == userId && x.Kategori == kategori)
            .Select(x => new CatalogSuggestionViewModel
            {
                Name = x.Ad,
                PosterUrl = NormalizePosterUrl(x.PosterUrl) ?? string.Empty,
                Genre = x.Tur ?? string.Empty,
                Summary = x.Konu ?? string.Empty,
                Score = x.Puan ?? string.Empty,
                Price = x.Fiyat ?? string.Empty
            })
            .ToListAsync();

        var local = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return RankFuzzy(query, local);
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchBooksAsync(string query, bool comicsOnly, string lang)
    {
        var open = await SearchOpenLibraryAsync(query, lang);
        var google = await SearchGoogleBooksAsync(query, comicsOnly, lang);

        var merged = open.Concat(google)
            .GroupBy(x => x.Name.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        if (comicsOnly)
        {
            var filtered = merged
                .Where(x =>
                    (x.Genre ?? string.Empty).Contains("comic", StringComparison.OrdinalIgnoreCase) ||
                    (x.Genre ?? string.Empty).Contains("graphic", StringComparison.OrdinalIgnoreCase) ||
                    (x.Genre ?? string.Empty).Contains("manga", StringComparison.OrdinalIgnoreCase) ||
                    (x.Genre ?? string.Empty).Contains("manhwa", StringComparison.OrdinalIgnoreCase) ||
                    (x.Name ?? string.Empty).Contains("comic", StringComparison.OrdinalIgnoreCase) ||
                    (x.Name ?? string.Empty).Contains("webtoon", StringComparison.OrdinalIgnoreCase) ||
                    (x.Name ?? string.Empty).Contains("batman", StringComparison.OrdinalIgnoreCase) ||
                    (x.Summary ?? string.Empty).Contains("comic", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (filtered.Count > 0)
            {
                merged = filtered;
            }
        }

        return RankFuzzy(query, merged);
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchGoogleBooksAsync(string query, bool comicsOnly, string lang)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var q = comicsOnly ? $"{query} comic graphic novel manga manhwa webtoon" : query;
        var url = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(q)}&maxResults=20";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("volumeInfo", out var vi))
                {
                    continue;
                }

                var title = vi.TryGetProperty("title", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var author = string.Empty;
                if (vi.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
                {
                    author = string.Join(", ", authors.EnumerateArray().Take(2).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
                }

                var genre = string.Empty;
                if (vi.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
                {
                    genre = string.Join(", ", categories.EnumerateArray().Take(3).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
                }

                var summary = vi.TryGetProperty("description", out var d) ? (d.GetString() ?? string.Empty) : string.Empty;
                var poster = string.Empty;
                if (vi.TryGetProperty("imageLinks", out var il) &&
                    (il.TryGetProperty("thumbnail", out var th) || il.TryGetProperty("smallThumbnail", out th)))
                {
                    poster = th.GetString() ?? string.Empty;
                }
                if (!string.IsNullOrWhiteSpace(poster))
                {
                    poster = poster.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
                }

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = author,
                    Genre = genre,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty,
                    Summary = summary,
                    Score = string.Empty,
                    Price = string.Empty
                });
            }

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchCartoonsAsync(string query, string lang)
    {
        var tvTask = SearchTvMazeAsync(query);
        var tvAnimationTask = SearchTvMazeAsync($"{query} animation");
        var moviesTask = SearchMoviesAsync(query);
        var moviesAnimationTask = SearchMoviesAsync($"{query} animation");
        var moviesStudiosTask = SearchMoviesAsync($"{query} disney pixar dreamworks illumination sony animation warner animation nickelodeon cartoon network");
        var local = SearchLocalCartoons(query, lang);

        await Task.WhenAll(tvTask, tvAnimationTask, moviesTask, moviesAnimationTask, moviesStudiosTask);

        var merged = tvTask.Result
            .Concat(tvAnimationTask.Result)
            .Concat(moviesTask.Result)
            .Concat(moviesAnimationTask.Result)
            .Concat(moviesStudiosTask.Result)
            .Concat(local)
            .GroupBy(x => x.Name.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        var filtered = merged
            .Where(x =>
                (x.Genre ?? string.Empty).Contains("animation", StringComparison.OrdinalIgnoreCase) ||
                (x.Genre ?? string.Empty).Contains("cartoon", StringComparison.OrdinalIgnoreCase) ||
                (x.Genre ?? string.Empty).Contains("anime", StringComparison.OrdinalIgnoreCase) ||
                (x.Genre ?? string.Empty).Contains("family", StringComparison.OrdinalIgnoreCase) ||
                (x.Summary ?? string.Empty).Contains("animation", StringComparison.OrdinalIgnoreCase) ||
                (x.Summary ?? string.Empty).Contains("animated", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("cartoon", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("anime", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("sponge", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("henry", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("tom", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("jerry", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("disney", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("pixar", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("dreamworks", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("nickelodeon", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("cartoon network", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return RankFuzzy(query, filtered.Count > 0 ? filtered : merged);
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchOpenLibraryAsync(string query, string lang)
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

                var summary = string.Empty;
                if (item.TryGetProperty("first_sentence", out var fs))
                {
                    if (fs.ValueKind == JsonValueKind.String)
                    {
                        summary = fs.GetString() ?? string.Empty;
                    }
                    else if (fs.ValueKind == JsonValueKind.Array && fs.GetArrayLength() > 0)
                    {
                        summary = fs[0].GetString() ?? string.Empty;
                    }
                }
                if (!string.IsNullOrWhiteSpace(year))
                {
                    var pub = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)
                        ? $"Published: {year}"
                        : $"Yayin: {year}";
                    summary = string.IsNullOrWhiteSpace(summary) ? pub : $"{summary}\n{pub}";
                }

                var cover = string.Empty;
                if (item.TryGetProperty("cover_i", out var coverId) && coverId.ValueKind == JsonValueKind.Number)
                {
                    cover = $"https://covers.openlibrary.org/b/id/{coverId.GetInt32()}-M.jpg";
                }
                else if (item.TryGetProperty("edition_key", out var editions) &&
                         editions.ValueKind == JsonValueKind.Array &&
                         editions.GetArrayLength() > 0)
                {
                    var edition = editions[0].GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(edition))
                    {
                        cover = $"https://covers.openlibrary.org/b/olid/{edition}-M.jpg";
                    }
                }
                else if (item.TryGetProperty("isbn", out var isbns) &&
                         isbns.ValueKind == JsonValueKind.Array &&
                         isbns.GetArrayLength() > 0)
                {
                    var isbn = isbns[0].GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(isbn))
                    {
                        cover = $"https://covers.openlibrary.org/b/isbn/{isbn}-M.jpg";
                    }
                }

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = author,
                    Genre = author,
                    PosterUrl = NormalizePosterUrl(cover) ?? string.Empty,
                    Summary = summary,
                    Score = string.Empty,
                    Price = string.Empty
                });
            }

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchTvMazeAsync(string query)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = $"https://api.tvmaze.com/search/shows?q={Uri.EscapeDataString(query)}";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("show", out var show))
                {
                    continue;
                }

                var title = show.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var genres = string.Empty;
                if (show.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array)
                {
                    genres = string.Join(", ", gs.EnumerateArray().Take(3).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
                }
                var summary = show.TryGetProperty("summary", out var sm) ? (sm.GetString() ?? string.Empty) : string.Empty;
                summary = StripHtml(summary);

                var rating = string.Empty;
                if (show.TryGetProperty("rating", out var ratingObj) &&
                    ratingObj.TryGetProperty("average", out var avg) &&
                    avg.ValueKind == JsonValueKind.Number)
                {
                    rating = avg.GetDouble().ToString("0.0");
                }

                var poster = string.Empty;
                if (show.TryGetProperty("image", out var imageObj) &&
                    imageObj.TryGetProperty("medium", out var medium))
                {
                    poster = medium.GetString() ?? string.Empty;
                }

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = genres,
                    Genre = genres,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty,
                    Summary = summary,
                    Score = rating,
                    Price = string.Empty
                });
            }

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchMoviesAsync(string query)
    {
        var normalized = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "film" or "flim" or "movie")
        {
            return await SearchYtsPopularAsync();
        }

        var ytsTask = SearchYtsAsync(query);
        var omdbTask = SearchOmdbAsync(query);
        var itunesTask = SearchItunesMoviesAsync(query);
        var imdbTask = SearchImdbSuggestionsAsync(query);
        await Task.WhenAll(ytsTask, omdbTask, itunesTask, imdbTask);

        var merged = ytsTask.Result
            .Concat(omdbTask.Result)
            .Concat(itunesTask.Result)
            .Concat(imdbTask.Result)
            .Concat(SearchLocalMovies(query))
            .GroupBy(x => x.Name.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        return RankFuzzy(query, merged);
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchYtsPopularAsync()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = "https://yts.mx/api/v2/list_movies.json?limit=20&sort_by=rating";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("movies", out var movies) ||
                movies.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var item in movies.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var score = item.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble().ToString("0.0") : string.Empty;
                var poster = item.TryGetProperty("medium_cover_image", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;
                var summary = item.TryGetProperty("summary", out var sm) ? (sm.GetString() ?? string.Empty) : string.Empty;
                var genres = string.Empty;
                if (item.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array)
                {
                    genres = string.Join(", ", gs.EnumerateArray().Take(3).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
                }

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    Genre = genres,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty,
                    Summary = summary,
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

    private async Task<List<CatalogSuggestionViewModel>> SearchYtsAsync(string query)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = $"https://yts.mx/api/v2/list_movies.json?query_term={Uri.EscapeDataString(query)}&limit=20&sort_by=rating";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("movies", out var movies) ||
                movies.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var item in movies.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var year = item.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32().ToString() : string.Empty;
                var score = item.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble().ToString("0.0") : string.Empty;
                var poster = item.TryGetProperty("medium_cover_image", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;
                var summary = item.TryGetProperty("summary", out var sm) ? (sm.GetString() ?? string.Empty) : string.Empty;
                var genres = string.Empty;
                if (item.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array)
                {
                    genres = string.Join(", ", gs.EnumerateArray().Take(3).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
                }

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = year,
                    Genre = genres,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty,
                    Summary = summary,
                    Score = score,
                    Price = string.Empty
                });
            }

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchOmdbAsync(string query)
    {
        var apiKey = Environment.GetEnvironmentVariable("OMDB_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new List<CatalogSuggestionViewModel>();
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = $"https://www.omdbapi.com/?apikey={Uri.EscapeDataString(apiKey)}&s={Uri.EscapeDataString(query)}&type=movie";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("Search", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("Title", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var year = item.TryGetProperty("Year", out var y) ? (y.GetString() ?? string.Empty) : string.Empty;
                var poster = item.TryGetProperty("Poster", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = year,
                    Genre = string.Empty,
                    PosterUrl = NormalizePosterUrl(poster == "N/A" ? string.Empty : poster) ?? string.Empty,
                    Summary = string.Empty,
                    Score = string.Empty,
                    Price = string.Empty
                });
            }

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchItunesMoviesAsync(string query)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&entity=movie&limit=20&country=us";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("trackName", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var poster = item.TryGetProperty("artworkUrl100", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;
                if (!string.IsNullOrWhiteSpace(poster))
                {
                    poster = poster.Replace("100x100bb", "300x300bb", StringComparison.OrdinalIgnoreCase);
                }

                var genre = item.TryGetProperty("primaryGenreName", out var g) ? (g.GetString() ?? string.Empty) : string.Empty;
                var summary = item.TryGetProperty("longDescription", out var ld)
                    ? (ld.GetString() ?? string.Empty)
                    : (item.TryGetProperty("shortDescription", out var sd) ? (sd.GetString() ?? string.Empty) : string.Empty);

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = string.Empty,
                    Genre = genre,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty,
                    Summary = summary,
                    Score = string.Empty,
                    Price = string.Empty
                });
            }

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchSteamAsync(string query, string lang)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var en = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        var storeLang = en ? "english" : "turkish";
        var cc = en ? "us" : "tr";
        var url = $"https://store.steampowered.com/api/storesearch?term={Uri.EscapeDataString(query)}&l={storeLang}&cc={cc}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "KategoriSecici/1.0");
            using var res = await client.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            await using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return await SearchSteamCommunityAppsAsync(query, lang);
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var appId = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt32()
                    : 0;

                var price = string.Empty;
                if (item.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Object &&
                    pr.TryGetProperty("final", out var finalPrice) && finalPrice.ValueKind == JsonValueKind.Number)
                {
                    price = en
                        ? $"${finalPrice.GetInt32() / 100.0:0.00}"
                        : $"?{finalPrice.GetInt32() / 100.0:0.00}";
                }
                else if (item.TryGetProperty("is_free", out var isFree) && isFree.ValueKind == JsonValueKind.True)
                {
                    price = en ? "Free" : "Ucretsiz";
                }
                var score = string.Empty;
                if (item.TryGetProperty("review_score", out var rv) && rv.ValueKind == JsonValueKind.Number)
                {
                    score = rv.GetInt32().ToString();
                }
                var genres = string.Empty;
                if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                {
                    genres = string.Join(", ", tags.EnumerateArray().Take(3).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
                }

                var poster = item.TryGetProperty("tiny_image", out var img) ? (img.GetString() ?? string.Empty) : string.Empty;
                var dto = new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = price,
                    Genre = genres,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty,
                    Summary = string.Empty,
                    Score = score,
                    Price = price
                };

                if (appId > 0 && (string.IsNullOrWhiteSpace(dto.Genre) || string.IsNullOrWhiteSpace(dto.Summary) || string.IsNullOrWhiteSpace(dto.Price)))
                {
                    var detail = await GetSteamAppDetailAsync(client, appId, lang);
                    if (detail is not null)
                    {
                        if (string.IsNullOrWhiteSpace(dto.Genre)) dto.Genre = detail.Value.Genre;
                        if (string.IsNullOrWhiteSpace(dto.Summary)) dto.Summary = detail.Value.Summary;
                        if (string.IsNullOrWhiteSpace(dto.Price)) dto.Price = detail.Value.Price;
                        if (string.IsNullOrWhiteSpace(dto.PosterUrl)) dto.PosterUrl = detail.Value.Poster;
                    }
                }

                list.Add(dto);
            }

            if (list.Count == 0)
            {
                return await SearchSteamCommunityAppsAsync(query, lang);
            }

            var fallback = await SearchSteamCommunityAppsAsync(query, lang);
            var merged = list.Concat(fallback)
                .GroupBy(x => x.Name.Trim().ToLowerInvariant())
                .Select(g => g.First())
                .ToList();
            return RankFuzzy(query, merged);
        }
        catch
        {
            return await SearchSteamCommunityAppsAsync(query, lang);
        }
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchImdbSuggestionsAsync(string query)
    {
        var q = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(q))
        {
            return new List<CatalogSuggestionViewModel>();
        }

        var first = char.IsLetterOrDigit(q[0]) ? q[0] : 'a';
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = $"https://v2.sg.media-imdb.com/suggestion/{first}/{Uri.EscapeDataString(q)}.json";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("d", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var item in items.EnumerateArray().Take(20))
            {
                var type = item.TryGetProperty("qid", out var qid) ? (qid.GetString() ?? string.Empty) : string.Empty;
                if (!string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(type, "feature", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var title = item.TryGetProperty("l", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var year = item.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32().ToString() : string.Empty;
                var poster = item.TryGetProperty("i", out var iObj) && iObj.TryGetProperty("imageUrl", out var iu)
                    ? (iu.GetString() ?? string.Empty)
                    : string.Empty;

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = year,
                    Genre = string.Empty,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty,
                    Summary = string.Empty,
                    Score = string.Empty,
                    Price = string.Empty
                });
            }

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchSteamCommunityAppsAsync(string query, string lang)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var en = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        var url = $"https://steamcommunity.com/actions/SearchApps/{Uri.EscapeDataString(query)}";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return SearchLocalGames(query, lang);
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var item in doc.RootElement.EnumerateArray().Take(20))
            {
                var title = item.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var appId = item.TryGetProperty("appid", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt32()
                    : 0;

                var dto = new CatalogSuggestionViewModel
                {
                    Name = title,
                    Genre = string.Empty,
                    PosterUrl = string.Empty,
                    Summary = string.Empty,
                    Score = string.Empty,
                    Price = string.Empty
                };

                if (appId > 0)
                {
                    var detail = await GetSteamAppDetailAsync(client, appId, lang);
                    if (detail is not null)
                    {
                        dto.Genre = detail.Value.Genre;
                        dto.Summary = detail.Value.Summary;
                        dto.Price = string.IsNullOrWhiteSpace(detail.Value.Price) ? (en ? "Free" : "Ucretsiz") : detail.Value.Price;
                        dto.PosterUrl = detail.Value.Poster;
                    }
                }

                list.Add(dto);
            }

            var merged = list.Concat(SearchLocalGames(query, lang))
                .GroupBy(x => x.Name.Trim().ToLowerInvariant())
                .Select(g => g.First())
                .ToList();

            return RankFuzzy(query, merged);
        }
        catch
        {
            return SearchLocalGames(query, lang);
        }
    }

    private static List<CatalogSuggestionViewModel> SearchLocalCartoons(string query, string lang)
    {
        var tr = !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        var local = new List<CatalogSuggestionViewModel>
        {
            new() { Name = "SpongeBob SquarePants", AltName = "Sunger Bob", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Nickelodeon'un Bikini Bottom maceralari." : "Nickelodeon's Bikini Bottom adventures.", Score = "8.2", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/81/202627.jpg" },
            new() { Name = "Henry Danger", AltName = "Risk Avcisi Henry", Genre = tr ? "Aile, Komedi, Cocuk" : "Family, Comedy, Kids", Summary = tr ? "Nickelodeon dizisi, Kaptan Man'in yardimcisi Henry'nin maceralari." : "Nickelodeon series about Henry's adventures as Captain Man's sidekick.", Score = "5.1", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/1/2605.jpg" },
            new() { Name = "The Loud House", AltName = "Gurultu Ailesi", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Nickelodeon aile-komedi animasyonu." : "Nickelodeon family-comedy animation.", Score = "7.1", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/103/258851.jpg" },
            new() { Name = "Avatar: The Last Airbender", AltName = "Avatar Son Hava Bukucu", Genre = tr ? "Animasyon, Macera, Fantastik" : "Animation, Adventure, Fantasy", Summary = tr ? "Nickelodeon efsanesi." : "Nickelodeon classic.", Score = "9.3", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/1/2608.jpg" },
            new() { Name = "Teenage Mutant Ninja Turtles", AltName = "Ninja Kaplumbagalar", Genre = tr ? "Animasyon, Aksiyon" : "Animation, Action", Summary = tr ? "Nickelodeon TMNT serisi." : "Nickelodeon TMNT series.", Score = "7.8", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/2/6756.jpg" },
            new() { Name = "Adventure Time", AltName = "Macera Zamani", Genre = tr ? "Animasyon, Fantastik" : "Animation, Fantasy", Summary = tr ? "Cartoon Network klasiği." : "A Cartoon Network classic.", Score = "8.6", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/9/96/Adventure_Time_-_Title_card.png" },
            new() { Name = "Regular Show", AltName = "Sira Disi", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Cartoon Network komedi serisi." : "Cartoon Network comedy series.", Score = "8.5", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/6/16903.jpg" },
            new() { Name = "Ben 10", AltName = "Ben Ten", Genre = tr ? "Animasyon, Aksiyon, Macera" : "Animation, Action, Adventure", Summary = tr ? "Cartoon Network aksiyon-macera serisi." : "Cartoon Network action-adventure series.", Score = "7.5", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/75/188428.jpg" },
            new() { Name = "The Amazing World of Gumball", AltName = "Gumball", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Cartoon Network modern animasyon hit'i." : "Cartoon Network modern animation hit.", Score = "8.3", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/132/332541.jpg" },
            new() { Name = "We Bare Bears", AltName = "Uc Ayicik", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Cartoon Network sevimli ayiciklar serisi." : "Cartoon Network adorable bear series.", Score = "7.9", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/54/136717.jpg" },
            new() { Name = "Phineas and Ferb", AltName = "Fineas ve Forb", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Disney Television Animation yapimi." : "Disney Television Animation production.", Score = "8.1", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/89/223318.jpg" },
            new() { Name = "Gravity Falls", AltName = "Esrarengiz Kasaba", Genre = tr ? "Animasyon, Gizem, Komedi" : "Animation, Mystery, Comedy", Summary = tr ? "Disney Channel/Disney Television Animation efsanesi." : "Disney Channel/Disney Television Animation classic.", Score = "8.9", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/49/123761.jpg" },
            new() { Name = "DuckTales", AltName = "Varyemezler", Genre = tr ? "Animasyon, Macera, Komedi" : "Animation, Adventure, Comedy", Summary = tr ? "Disney Television Animation serisi." : "Disney Television Animation series.", Score = "8.2", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/113/282455.jpg" },
            new() { Name = "Kim Possible", Genre = tr ? "Animasyon, Aksiyon, Komedi" : "Animation, Action, Comedy", Summary = tr ? "Disney Channel animasyon klasigi." : "Disney Channel animation classic.", Score = "7.2", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/232/580356.jpg" },
            new() { Name = "The Owl House", AltName = "Baykus Evi", Genre = tr ? "Animasyon, Fantastik" : "Animation, Fantasy", Summary = tr ? "Disney Television Animation modern serisi." : "Disney Television Animation modern series.", Score = "8.5", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/238/596972.jpg" },
            new() { Name = "Amphibia", Genre = tr ? "Animasyon, Fantastik, Macera" : "Animation, Fantasy, Adventure", Summary = tr ? "Disney Television Animation." : "Disney Television Animation.", Score = "8.0", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/225/564634.jpg" },
            new() { Name = "Frozen", AltName = "Karlar Ulkesi", Genre = tr ? "Animasyon, Muzikal" : "Animation, Musical", Summary = tr ? "Disney prensesleri Elsa ve Anna'nin hikayesi." : "Disney princess story of Elsa and Anna.", Score = "7.4", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/0/05/Frozen_%282013_film%29_poster.jpg" },
            new() { Name = "Moana", AltName = "Vaiana", Genre = tr ? "Animasyon, Macera" : "Animation, Adventure", Summary = tr ? "Disney animasyon filmi." : "Disney animated feature.", Score = "7.6", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/2/26/Moana_Teaser_Poster.jpg" },
            new() { Name = "Encanto", Genre = tr ? "Animasyon, Muzikal" : "Animation, Musical", Summary = tr ? "Disney aile temali animasyon filmi." : "Disney family-themed animated feature.", Score = "7.2", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/1/1d/Encanto_poster.jpeg" },
            new() { Name = "Zootopia", AltName = "Zootropolis", Genre = tr ? "Animasyon, Komedi, Macera" : "Animation, Comedy, Adventure", Summary = tr ? "Disney polisiye-komedi animasyonu." : "Disney buddy-cop animation.", Score = "8.0", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/9/96/Zootopia_%28movie_poster%29.jpg" },
            new() { Name = "Tangled", AltName = "Rapunzel", Genre = tr ? "Animasyon, Macera, Komedi" : "Animation, Adventure, Comedy", Summary = tr ? "Disney prenses animasyonu." : "Disney princess animation.", Score = "7.7", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/a/a8/Tangled_poster.jpg" },
            new() { Name = "Bolt", Genre = tr ? "Animasyon, Aile" : "Animation, Family", Summary = tr ? "Disney'in Bolt animasyon filmi." : "Disney's Bolt animated film.", Score = "6.8", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/4/44/Bolt_poster.jpg" },
            new() { Name = "Toy Story", AltName = "Oyuncak Hikayesi", Genre = tr ? "Animasyon, Aile" : "Animation, Family", Summary = tr ? "Pixar'in oyuncaklar dunyasi." : "Pixar's world of toys.", Score = "8.3", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/1/13/Toy_Story.jpg" },
            new() { Name = "Cars", AltName = "Arabalar", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Pixar'in yaris dunyasi." : "Pixar racing world.", Score = "7.2", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/3/34/Cars_2006.jpg" },
            new() { Name = "Coco", Genre = tr ? "Animasyon, Aile, Muzik" : "Animation, Family, Music", Summary = tr ? "Pixar'in muzik ve aile temali filmi." : "Pixar's music and family themed film.", Score = "8.4", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/9/90/Coco_%282017_film%29_poster.jpg" },
            new() { Name = "Inside Out", AltName = "Ters Yuz", Genre = tr ? "Animasyon, Aile, Komedi" : "Animation, Family, Comedy", Summary = tr ? "Pixar'in duygular dunyasi." : "Pixar's world of emotions.", Score = "8.1", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/f/f7/Inside_Out_%282015_film%29_poster.jpg" },
            new() { Name = "Finding Nemo", AltName = "Kayıp Balik Nemo", Genre = tr ? "Animasyon, Macera" : "Animation, Adventure", Summary = tr ? "Pixar deniz macerasi." : "Pixar sea adventure.", Score = "8.2", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/2/29/Finding_Nemo.jpg" },
            new() { Name = "WALL-E", Genre = tr ? "Animasyon, Bilim Kurgu" : "Animation, Sci-Fi", Summary = tr ? "Pixar'in robot hikayesi." : "Pixar robot story.", Score = "8.4", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/c/c2/WALL-Eposter.jpg" },
            new() { Name = "Up", AltName = "Yukari Bak", Genre = tr ? "Animasyon, Macera" : "Animation, Adventure", Summary = tr ? "Pixar klasik macera filmi." : "Pixar classic adventure film.", Score = "8.3", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/0/05/Up_%282009_film%29.jpg" },
            new() { Name = "Shrek", AltName = "Srek", Genre = tr ? "Animasyon, Komedi, Macera" : "Animation, Comedy, Adventure", Summary = tr ? "DreamWorks klasik animasyon serisi." : "DreamWorks classic animated franchise.", Score = "7.9", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/3/39/Shrek.jpg" },
            new() { Name = "Puss in Boots", AltName = "Cizmeli Kedi", Genre = tr ? "Animasyon, Macera" : "Animation, Adventure", Summary = tr ? "DreamWorks'un Cizmeli Kedi macerasi." : "DreamWorks' Puss in Boots adventure.", Score = "7.0", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/6/6d/Puss_in_Boots_2011_poster.jpg" },
            new() { Name = "Kung Fu Panda", Genre = tr ? "Animasyon, Aksiyon" : "Animation, Action", Summary = tr ? "DreamWorks'ten Po'nun efsane yolculugu." : "Po's legendary journey from DreamWorks.", Score = "7.6", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/7/76/Kungfupanda.jpg" },
            new() { Name = "How to Train Your Dragon", AltName = "Ejderhani Nasil Egitirsin", Genre = tr ? "Animasyon, Fantastik, Macera" : "Animation, Fantasy, Adventure", Summary = tr ? "DreamWorks ejderha serisi." : "DreamWorks dragon series.", Score = "8.1", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/9/99/How_to_Train_Your_Dragon_Poster.jpg" },
            new() { Name = "Madagascar", Genre = tr ? "Animasyon, Komedi, Macera" : "Animation, Comedy, Adventure", Summary = tr ? "DreamWorks hayvanat bahcesi macerasi." : "DreamWorks zoo adventure.", Score = "6.9", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/6/60/Madagascar_%282005_film%29.jpg" },
            new() { Name = "Trolls", Genre = tr ? "Animasyon, Muzikal, Komedi" : "Animation, Musical, Comedy", Summary = tr ? "DreamWorks muzikli animasyon serisi." : "DreamWorks musical animation franchise.", Score = "6.4", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/a/a0/Trolls_%28film%29_poster.jpg" },
            new() { Name = "The Boss Baby", AltName = "Patron Bebek", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "DreamWorks komedi filmi." : "DreamWorks comedy film.", Score = "6.3", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/0/0e/The_Boss_Baby_poster.jpg" },
            new() { Name = "Spider-Man: Into the Spider-Verse", AltName = "Spider Verse", Genre = tr ? "Animasyon, Aksiyon" : "Animation, Action", Summary = tr ? "Sony Pictures Animation yapimi Oscarli film." : "Sony Pictures Animation Oscar-winning film.", Score = "8.4", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/f/fb/Spider-Man_Into_the_Spider-Verse_poster.jpg" },
            new() { Name = "Hotel Transylvania", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Sony Pictures Animation canavar komedisi." : "Sony Pictures Animation monster comedy.", Score = "7.0", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/2/20/Hotel_Transylvania_Poster.jpg" },
            new() { Name = "Cloudy with a Chance of Meatballs", AltName = "Kofte Yagmuru", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Sony Pictures Animation komedisi." : "Sony Pictures Animation comedy.", Score = "6.9", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/0/01/Cloudy_with_a_Chance_of_Meatballs_poster.jpg" },
            new() { Name = "The Smurfs", AltName = "Sirinler", Genre = tr ? "Animasyon, Aile, Komedi" : "Animation, Family, Comedy", Summary = tr ? "Sony yapimi Sirinler filmi." : "Sony produced Smurfs film.", Score = "5.4", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/3/35/The_Smurfs_poster.jpg" },
            new() { Name = "Despicable Me", AltName = "Cilgin Hirsiz", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Illumination'in Minion evreni." : "Illumination's Minion universe.", Score = "7.6", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/d/db/Despicable_Me_Poster.jpg" },
            new() { Name = "Minions", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Illumination Minions filmi." : "Illumination Minions film.", Score = "6.4", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/3/3d/Minions_poster.jpg" },
            new() { Name = "Sing", Genre = tr ? "Animasyon, Muzikal, Komedi" : "Animation, Musical, Comedy", Summary = tr ? "Illumination muzik yarismasi filmi." : "Illumination singing contest film.", Score = "7.1", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/8/8f/Sing_%282016_animated_film%29_poster.jpg" },
            new() { Name = "The Secret Life of Pets", AltName = "Evcil Hayvanlarin Gizli Yasami", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Illumination evcil hayvan komedisi." : "Illumination pet comedy.", Score = "6.5", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/6/64/The_Secret_Life_of_Pets_poster.jpg" },
            new() { Name = "The Super Mario Bros. Movie", AltName = "Super Mario Bros Filmi", Genre = tr ? "Animasyon, Macera, Komedi" : "Animation, Adventure, Comedy", Summary = tr ? "Illumination ve Nintendo ortak yapimi." : "Illumination and Nintendo co-production.", Score = "7.1", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/4/44/The_Super_Mario_Bros._Movie_poster.jpg" },
            new() { Name = "PAW Patrol: The Movie", AltName = "Pati Devriyesi", Genre = tr ? "Animasyon, Aile" : "Animation, Family", Summary = tr ? "Paramount/Nickelodeon animasyon filmi." : "Paramount/Nickelodeon animated movie.", Score = "6.1", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/0/0f/PAW_Patrol_The_Movie_poster.jpg" },
            new() { Name = "Teenage Mutant Ninja Turtles: Mutant Mayhem", AltName = "Mutant Kargasasi", Genre = tr ? "Animasyon, Aksiyon" : "Animation, Action", Summary = tr ? "Paramount Animation yapimi TMNT filmi." : "TMNT animated feature by Paramount Animation.", Score = "7.2", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/7/75/Teenage_Mutant_Ninja_Turtles_Mutant_Mayhem_poster.jpg" },
            new() { Name = "Looney Tunes", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Warner Bros. Animation klasigi." : "Warner Bros. Animation classic.", Score = "7.5", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/7/75/Looney_Tunes_golden_collection.jpg" },
            new() { Name = "Tom and Jerry", Genre = tr ? "Animasyon, Komedi" : "Animation, Comedy", Summary = tr ? "Warner Bros. Animation klasik serisi." : "Warner Bros. Animation classic series.", Score = "8.0", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/f/f6/TomandJerryTitleCardc.jpg" },
            new() { Name = "Scooby-Doo", Genre = tr ? "Animasyon, Gizem" : "Animation, Mystery", Summary = tr ? "Scooby ve ekibi gizemleri cozer." : "Scooby and team solve mysteries.", Score = "7.6", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/5/53/Scooby-Doo%21_Mystery_Incorporated_title_card.png" },
            new() { Name = "Teen Titans Go!", AltName = "Genc Titanlar", Genre = tr ? "Animasyon, Komedi, Aksiyon" : "Animation, Comedy, Action", Summary = tr ? "Cartoon Network/Warner ortak evreni." : "Cartoon Network/Warner shared universe.", Score = "5.7", PosterUrl = "https://static.tvmaze.com/uploads/images/medium_portrait/43/109351.jpg" }
        };
        return RankFuzzy(query, local);
    }

    private static List<CatalogSuggestionViewModel> SearchLocalMovies(string query)
    {
        var local = new List<CatalogSuggestionViewModel>
        {
            new() { Name = "The Godfather", Genre = "Crime, Drama", Summary = "The aging patriarch of an organized crime dynasty transfers control to his reluctant son.", Score = "9.2", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/1/1c/Godfather_ver1.jpg" },
            new() { Name = "Interstellar", Genre = "Adventure, Drama, Sci-Fi", Summary = "A team of explorers travel through a wormhole in space.", Score = "8.7", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/b/bc/Interstellar_film_poster.jpg" },
            new() { Name = "The Dark Knight", Genre = "Action, Crime, Drama", Summary = "Batman faces the Joker in Gotham.", Score = "9.0", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/8/8a/Dark_Knight.jpg" },
            new() { Name = "Inception", Genre = "Action, Sci-Fi, Thriller", Summary = "A thief steals information by infiltrating dreams.", Score = "8.8", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/7/7f/Inception_ver3.jpg" }
        };
        return RankFuzzy(query, local);
    }

    private static List<CatalogSuggestionViewModel> SearchLocalGames(string query, string lang)
    {
        var tr = !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        var local = new List<CatalogSuggestionViewModel>
        {
            new() { Name = "Zula", Genre = tr ? "Aksiyon, Nisanci" : "Action, Shooter", Summary = tr ? "Turk yapimi rekabetci FPS oyunu." : "A competitive FPS game.", Price = tr ? "Ucretsiz" : "Free", PosterUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/513710/header.jpg" },
            new() { Name = "Apex Legends", Genre = tr ? "Aksiyon, Battle Royale" : "Action, Battle Royale", Summary = tr ? "Takim tabanli battle royale nişanci oyunu." : "Squad-based battle royale shooter.", Price = tr ? "Ucretsiz" : "Free", PosterUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1172470/header.jpg" }
        };
        return RankFuzzy(query, local);
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchComicsAsync(string query, string lang)
    {
        var google = await SearchGoogleBooksAsync($"{query} comic graphic novel dc marvel", comicsOnly: true, lang);
        var open = await SearchOpenLibraryAsync($"{query} comic", lang);
        var local = new List<CatalogSuggestionViewModel>
        {
            new() { Name = "Batman: Year One", Genre = "Comic, Superhero", Summary = "Bruce Wayne'in Batman olarak ilk donemi.", PosterUrl = "https://covers.openlibrary.org/b/isbn/9781401207526-M.jpg", Score = string.Empty },
            new() { Name = "The Killing Joke", Genre = "Comic, Superhero", Summary = "Batman ve Joker'in karanlik hikayesi.", PosterUrl = "https://covers.openlibrary.org/b/isbn/9781401216672-M.jpg", Score = string.Empty }
        };

        var merged = google.Concat(open).Concat(local)
            .GroupBy(x => x.Name.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
        return RankFuzzy(query, merged);
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchWebtoonAsync(string query, string lang)
    {
        var kitsu = await SearchKitsuMangaAsync(query);
        var jikan = await SearchJikanAsync(query, anime: false);
        var local = new List<CatalogSuggestionViewModel>
        {
            new() { Name = "Solo Leveling", Genre = "Webtoon, Action, Fantasy", Summary = "Zayif bir avcinin guclenme hikayesi.", Score = "8.8", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/9/99/Solo_Leveling_Webtoon.png" },
            new() { Name = "Tower of God", Genre = "Webtoon, Adventure, Fantasy", Summary = "Kulenin zirvesine cikmak isteyenlerin hikayesi.", Score = "8.5", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/thumb/7/7e/Tower_of_God_%28manhwa%29.jpg/330px-Tower_of_God_%28manhwa%29.jpg" },
            new() { Name = "The God of High School", Genre = "Webtoon, Action", Summary = "Turnuvada mucadele eden genc dovusculer.", Score = "7.9", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/3/35/The_God_of_High_School.jpg" }
        };

        var merged = kitsu.Concat(jikan).Concat(local)
            .GroupBy(x => x.Name.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
        return RankFuzzy(query, merged);
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchKitsuMangaAsync(string query)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = $"https://kitsu.io/api/edge/manga?filter[text]={Uri.EscapeDataString(query)}&page[limit]=20";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var row in items.EnumerateArray())
            {
                if (!row.TryGetProperty("attributes", out var at))
                {
                    continue;
                }

                var title = at.TryGetProperty("canonicalTitle", out var ct) ? (ct.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var synopsis = at.TryGetProperty("synopsis", out var sy) ? (sy.GetString() ?? string.Empty) : string.Empty;
                var subtype = at.TryGetProperty("subtype", out var st) ? (st.GetString() ?? string.Empty) : string.Empty;
                var score = at.TryGetProperty("averageRating", out var ar) ? (ar.GetString() ?? string.Empty) : string.Empty;
                var poster = string.Empty;
                if (at.TryGetProperty("posterImage", out var pi) && pi.TryGetProperty("small", out var small))
                {
                    poster = small.GetString() ?? string.Empty;
                }

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    Genre = string.IsNullOrWhiteSpace(subtype) ? "Webtoon" : $"Webtoon, {subtype}",
                    Summary = synopsis,
                    Score = score,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty
                });
            }

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private async Task<List<CatalogSuggestionViewModel>> LocalizeCatalogItemsAsync(List<CatalogSuggestionViewModel> items, string lang)
    {
        if (items.Count == 0)
        {
            return items;
        }

        foreach (var item in items.Take(8))
        {
            item.Genre = await TranslateIfNeededAsync(item.Genre, lang);
            item.Summary = await TranslateIfNeededAsync(item.Summary, lang);
        }

        return items;
    }

    private async Task<string> TranslateIfNeededAsync(string? text, string targetLang)
    {
        var value = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lang = NormalizeLang(targetLang);
        var hasTurkishChars = value.IndexOfAny(new[] { 'ç', 'ğ', 'ı', 'ö', 'ş', 'ü', 'Ç', 'Ğ', 'İ', 'Ö', 'Ş', 'Ü' }) >= 0;

        if (lang == "tr" && hasTurkishChars)
        {
            return value;
        }

        if (lang == "en" && !hasTurkishChars)
        {
            return value;
        }

        var cacheKey = $"{lang}|{value}";
        if (TranslationCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={lang}&dt=t&q={Uri.EscapeDataString(value)}";
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return value;
            }

            var sb = new List<string>();
            var chunks = doc.RootElement[0];
            if (chunks.ValueKind == JsonValueKind.Array)
            {
                foreach (var ch in chunks.EnumerateArray())
                {
                    if (ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0)
                    {
                        var part = ch[0].GetString();
                        if (!string.IsNullOrWhiteSpace(part))
                        {
                            sb.Add(part);
                        }
                    }
                }
            }

            var translated = string.Join(string.Empty, sb).Trim();
            if (string.IsNullOrWhiteSpace(translated))
            {
                return value;
            }

            TranslationCache[cacheKey] = translated;
            return translated;
        }
        catch
        {
            return value;
        }
    }

    private static async Task<(string Genre, string Summary, string Price, string Poster)?> GetSteamAppDetailAsync(HttpClient client, int appId, string lang)
    {
        var en = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        var storeLang = en ? "english" : "turkish";
        var cc = en ? "us" : "tr";
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={storeLang}&cc={cc}";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty(appId.ToString(), out var appNode) ||
                !appNode.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True ||
                !appNode.TryGetProperty("data", out var data))
            {
                return null;
            }

            var genre = string.Empty;
            if (data.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            {
                genre = string.Join(", ",
                    genres.EnumerateArray()
                        .Take(3)
                        .Select(x => x.TryGetProperty("description", out var d) ? d.GetString() : null)
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            var summary = data.TryGetProperty("short_description", out var sd) ? (sd.GetString() ?? string.Empty) : string.Empty;
            var price = string.Empty;
            if (data.TryGetProperty("price_overview", out var po) &&
                po.TryGetProperty("final_formatted", out var ff))
            {
                price = ff.GetString() ?? string.Empty;
            }

            var poster = data.TryGetProperty("header_image", out var hi) ? (hi.GetString() ?? string.Empty) : string.Empty;
            return (genre, summary, price, NormalizePosterUrl(poster) ?? string.Empty);
        }
        catch
        {
            return null;
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

    private static bool IsAcceptableMatch(string input, string candidate)
    {
        static string N(string v) =>
            new((v ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                .ToArray());

        var a = N(input);
        var b = N(candidate);
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        if (a == b)
        {
            return true;
        }

        if (b.Contains(a, StringComparison.Ordinal) || a.Contains(b, StringComparison.Ordinal))
        {
            return true;
        }

        return Similarity(a, b) >= 0.45;
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

    private static string StripHtml(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return Regex.Replace(input, "<.*?>", string.Empty).Trim();
    }
}
