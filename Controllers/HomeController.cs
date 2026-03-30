using System.Diagnostics;
using System.Security.Claims;
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
            PosterUrl = NormalizePosterUrl(CleanValue(form.YeniOgePosterUrl, 600)),
            Tur = CleanValue(form.YeniOgeTur, 240),
            Konu = CleanValue(form.YeniOgeKonu, 3000),
            Puan = CleanValue(form.YeniOgePuan, 80),
            Fiyat = CleanValue(form.YeniOgeFiyat, 80),
            AppUserId = userId.Value,
            Izlendi = false
        };

        await FillMissingMetadataAsync(yeniKayit);

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

        if (query.Length < 2)
        {
            return Json(new
            {
                items = Array.Empty<CatalogSuggestionViewModel>(),
                message = currentLang == "en" ? "Type at least 2 characters." : "En az 2 karakter yaz."
            });
        }

        var providerResults = await SearchByCategoryAsync(kategori, query);

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

        await EnrichMissingMetadataInCategoryAsync(seciliKategori, userId);

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

    private async Task<List<CatalogSuggestionViewModel>> SearchByCategoryAsync(MedyaKategori kategori, string query)
    {
        var all = new List<CatalogSuggestionViewModel>();
        foreach (var q in BuildQueryVariants(query))
        {
            List<CatalogSuggestionViewModel> part = kategori switch
            {
                MedyaKategori.Anime => await SearchJikanAsync(q, true),
                MedyaKategori.Manga => await SearchJikanAsync(q, false),
                MedyaKategori.Kitap => await SearchBooksAsync(q, comicsOnly: false),
                MedyaKategori.Dizi => await SearchTvMazeAsync(q),
                MedyaKategori.Film => await SearchMoviesAsync(q),
                MedyaKategori.Oyun => await SearchSteamAsync(q),
                MedyaKategori.CizgiFilm => await SearchCartoonsAsync(q),
                MedyaKategori.CizgiRoman => await SearchBooksAsync(q, comicsOnly: true),
                _ => new List<CatalogSuggestionViewModel>()
            };

            all.AddRange(part);
            if (all.Count >= 60)
            {
                break;
            }
        }

        return RankFuzzy(query, all)
            .GroupBy(x => x.Name.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
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

        if (row.Kategori == MedyaKategori.Kitap || row.Kategori == MedyaKategori.CizgiRoman)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(row.Puan);
    }

    private async Task EnrichMissingMetadataInCategoryAsync(MedyaKategori kategori, int userId)
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
            changed |= await FillMissingMetadataAsync(item);
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync();
        }
    }

    private async Task<bool> FillMissingMetadataAsync(MedyaOgesi item)
    {
        var suggestions = item.Kategori switch
        {
            MedyaKategori.Anime => await SearchJikanAsync(item.Ad, true),
            MedyaKategori.Manga => await SearchJikanAsync(item.Ad, false),
            MedyaKategori.Kitap => await SearchBooksAsync(item.Ad, comicsOnly: false),
            MedyaKategori.Dizi => await SearchTvMazeAsync(item.Ad),
            MedyaKategori.Film => await SearchMoviesAsync(item.Ad),
            MedyaKategori.Oyun => await SearchSteamAsync(item.Ad),
            MedyaKategori.CizgiFilm => await SearchCartoonsAsync(item.Ad),
            MedyaKategori.CizgiRoman => await SearchBooksAsync(item.Ad, comicsOnly: true),
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
            item.Tur = CleanValue(best.Genre, 240);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(item.Konu) && !string.IsNullOrWhiteSpace(best.Summary))
        {
            item.Konu = CleanValue(best.Summary, 3000);
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
        else if (item.Kategori != MedyaKategori.Kitap && item.Kategori != MedyaKategori.CizgiRoman)
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
            .Where(x => x.Score >= 0.16)
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

    private async Task<List<CatalogSuggestionViewModel>> SearchBooksAsync(string query, bool comicsOnly)
    {
        var open = await SearchOpenLibraryAsync(query);
        var google = await SearchGoogleBooksAsync(query, comicsOnly);

        var merged = open.Concat(google)
            .GroupBy(x => x.Name.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        if (comicsOnly)
        {
            merged = merged
                .Where(x =>
                    (x.Genre ?? string.Empty).Contains("comic", StringComparison.OrdinalIgnoreCase) ||
                    (x.Genre ?? string.Empty).Contains("graphic", StringComparison.OrdinalIgnoreCase) ||
                    (x.Name ?? string.Empty).Contains("comic", StringComparison.OrdinalIgnoreCase) ||
                    (x.Summary ?? string.Empty).Contains("comic", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return RankFuzzy(query, merged);
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchGoogleBooksAsync(string query, bool comicsOnly)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var q = comicsOnly ? $"{query} comic graphic novel" : query;
        var url = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(q)}&maxResults=20&langRestrict={(comicsOnly ? "en" : "tr")}";
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
                if (vi.TryGetProperty("imageLinks", out var il) && il.TryGetProperty("thumbnail", out var th))
                {
                    poster = th.GetString() ?? string.Empty;
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

    private async Task<List<CatalogSuggestionViewModel>> SearchCartoonsAsync(string query)
    {
        var tv = await SearchTvMazeAsync($"{query} animation");
        var movies = await SearchMoviesAsync($"{query} animation");

        var merged = tv.Concat(movies)
            .GroupBy(x => x.Name.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        var filtered = merged
            .Where(x =>
                (x.Genre ?? string.Empty).Contains("animation", StringComparison.OrdinalIgnoreCase) ||
                (x.Summary ?? string.Empty).Contains("animation", StringComparison.OrdinalIgnoreCase) ||
                (x.Name ?? string.Empty).Contains("cartoon", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return RankFuzzy(query, filtered.Count > 0 ? filtered : merged);
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
                if (string.IsNullOrWhiteSpace(summary) && !string.IsNullOrWhiteSpace(year))
                {
                    summary = $"Yayin: {year}";
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
        var fromYts = await SearchYtsAsync(query);
        if (fromYts.Count > 0)
        {
            return fromYts;
        }

        var fromOmdb = await SearchOmdbAsync(query);
        if (fromOmdb.Count > 0)
        {
            return fromOmdb;
        }

        return await SearchItunesMoviesAsync(query);
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

    private async Task<List<CatalogSuggestionViewModel>> SearchSteamAsync(string query)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = $"https://store.steampowered.com/api/storesearch?term={Uri.EscapeDataString(query)}&l=turkish&cc=tr";
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
                return new List<CatalogSuggestionViewModel>();
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
                    price = $"₺{finalPrice.GetInt32() / 100.0:0.00}";
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
                    var detail = await GetSteamAppDetailAsync(client, appId);
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

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private static async Task<(string Genre, string Summary, string Price, string Poster)?> GetSteamAppDetailAsync(HttpClient client, int appId)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=turkish&cc=tr";
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
