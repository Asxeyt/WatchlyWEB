using System.Diagnostics;
using System.Security.Claims;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
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
        return User.Identity?.IsAuthenticated == true
            ? RedirectToAction(nameof(Index), new { lang = currentLang, kategori = MedyaKategori.Film })
            : RedirectToAction("Login", "Account", new { lang = currentLang });
    }

    [HttpGet]
    public IActionResult Landing(string lang = "tr")
    {
        return Anasayfa(lang);
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
    public async Task<IActionResult> IzledimYap(int id, MedyaKategori kategori, string dil = "tr", int? degerlendirmeSeviyesi = null)
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

        var level = NormalizeDegerlendirmeSeviyesi(degerlendirmeSeviyesi);
        if (level == 6)
        {
            var sameCategoryMuks = await _dbContext.MedyaOgeleri
                .Where(x => x.AppUserId == userId.Value && x.Kategori == kategori && x.Id != kayit.Id && x.DegerlendirmeSeviyesi == 6)
                .ToListAsync();

            foreach (var row in sameCategoryMuks)
            {
                row.DegerlendirmeSeviyesi = 5;
            }
        }

        kayit.Izlendi = true;
        kayit.DegerlendirmeSeviyesi = level;
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
        kayit.DegerlendirmeSeviyesi = null;
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
        var results = MergeCatalogItems(listResults.Concat(providerResults))
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
    public async Task<IActionResult> Detay(string lang = "tr", MedyaKategori kategori = MedyaKategori.Film, int? id = null, string? ad = null)
    {
        var currentLang = NormalizeLang(lang);
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return RedirectToAction("Login", "Account", new { lang = currentLang });
        }

        var detail = await BuildDetayViewModelAsync(currentLang, kategori, userId.Value, id, ad);
        if (detail is null)
        {
            TempData["Mesaj"] = currentLang == "en" ? "Content not found." : "Icerik bulunamadi.";
            TempData["MesajTipi"] = "warning";
            return RedirectToAction(nameof(Index), new { lang = currentLang, kategori });
        }

        ViewData["Lang"] = currentLang;
        ViewData["SelectedCategory"] = kategori.ToString();
        ViewData["BodyClass"] = "detail-page";
        return View(detail);
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

    private async Task<MedyaDetayViewModel?> BuildDetayViewModelAsync(string lang, MedyaKategori kategori, int userId, int? id, string? ad)
    {
        MedyaOgesi? row = null;
        if (id.HasValue)
        {
            row = await _dbContext.MedyaOgeleri.FirstOrDefaultAsync(x => x.Id == id.Value && x.AppUserId == userId);
        }

        if (row is null && !string.IsNullOrWhiteSpace(ad))
        {
            var rawName = ad.Trim();
            row = await _dbContext.MedyaOgeleri
                .FirstOrDefaultAsync(x => x.AppUserId == userId && x.Kategori == kategori && x.Ad.ToLower() == rawName.ToLower());
        }

        var name = row?.Ad ?? ad?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var suggestions = await SearchByCategoryAsync(kategori, name, lang);
        var best = suggestions.FirstOrDefault() ?? new CatalogSuggestionViewModel { Name = name };
        var extras = await FetchDetailExtrasAsync(kategori, name, lang);
        if (NeedsExtraFallback(kategori, extras) &&
            !string.IsNullOrWhiteSpace(best.Name) &&
            !string.Equals(best.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            var byBestName = await FetchDetailExtrasAsync(kategori, best.Name, lang);
            extras = MergeExtras(extras, byBestName);
        }

        if (NeedsExtraFallback(kategori, extras))
        {
            var altNames = suggestions
                .Select(x => x.Name?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => !string.Equals(x, name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();

            foreach (var alt in altNames)
            {
                if (string.IsNullOrWhiteSpace(alt))
                {
                    continue;
                }

                var byAlt = await FetchDetailExtrasAsync(kategori, alt, lang);
                extras = MergeExtras(extras, byAlt);
                if (!NeedsExtraFallback(kategori, extras))
                {
                    break;
                }
            }
        }

        var trailer = FirstNonEmpty(extras.TrailerUrl, best.TrailerUrl);
        var trailerEmbed = ToEmbedUrl(trailer);
        var isDirectVideo = IsDirectVideo(trailerEmbed);

        var genre = row?.Tur;
        if (string.IsNullOrWhiteSpace(genre))
        {
            genre = FirstNonEmpty(best.Genre, extras.Genre);
        }
        genre = await TranslateIfNeededAsync(genre, lang);

        var summary = row?.Konu;
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = FirstNonEmpty(best.Summary, extras.Summary);
        }
        summary = await TranslateIfNeededAsync(summary, lang);

        var release = FirstNonEmpty(best.ReleaseDate, extras.ReleaseDate);
        var creator = await TranslateIfNeededAsync(FirstNonEmpty(best.Creator, extras.Creator), lang);
        var castText = await TranslateIfNeededAsync(FirstNonEmpty(best.Cast, extras.Cast), lang);

        var model = new MedyaDetayViewModel
        {
            Dil = lang,
            Kategori = kategori,
            KayitId = row?.Id,
            ListedeMi = row is not null && !row.Izlendi,
            TamamlandiMi = row?.Izlendi ?? false,
            Ad = row?.Ad ?? best.Name,
            PosterUrl = row?.PosterUrl ?? best.PosterUrl,
            Tur = genre ?? string.Empty,
            Konu = summary ?? string.Empty,
            Puan = row?.Puan ?? best.Score,
            Fiyat = row?.Fiyat ?? best.Price,
            YayinTarihi = release ?? string.Empty,
            Oyuncular = castText ?? string.Empty,
            Yapimci = creator ?? string.Empty,
            FragmanUrl = trailer,
            FragmanEmbedUrl = trailerEmbed,
            FragmanDogrudanVideo = isDirectVideo,
            Gorseller = extras.Images,
            OyuncuKartlari = extras.CastCards
        };

        ApplyDetailMetadataFallbacks(model);

        if (string.IsNullOrWhiteSpace(model.FragmanUrl))
        {
            model.FragmanUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(model.Ad + " trailer")}";
        }

        var benzer = await BuildSimilarSuggestionsAsync(kategori, model.Ad, model.Tur, lang);
        model.BenzerIcerikler = benzer
            .Where(x => !string.Equals(x.Name, model.Ad, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();

        return model;
    }

    private static string BuildSimilarQuery(string name)
    {
        var words = (name ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return name ?? string.Empty;
        }

        if (words.Length == 1)
        {
            return words[0];
        }

        return string.Join(' ', words.Take(2));
    }

    private async Task<List<CatalogSuggestionViewModel>> BuildSimilarSuggestionsAsync(MedyaKategori kategori, string name, string? genre, string lang)
    {
        if (kategori == MedyaKategori.Anime)
        {
            var anime = await BuildAnimeSimilarSuggestionsAsync(name, lang);
            var animeFiltered = anime
                .Where(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (animeFiltered.Count > 0)
            {
                return animeFiltered;
            }
        }

        var genreToken = (genre ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(genreToken))
        {
            var byGenre = await SearchByCategoryAsync(kategori, genreToken, lang);
            var byGenreFiltered = byGenre
                .Where(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byGenreFiltered.Count > 0)
            {
                return byGenreFiltered;
            }
        }

        var byName = await SearchByCategoryAsync(kategori, BuildSimilarQuery(name), lang);
        var byNameFiltered = byName
            .Where(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byNameFiltered.Count > 0)
        {
            return byNameFiltered;
        }

        var tokens = name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        foreach (var token in tokens)
        {
            var byToken = await SearchByCategoryAsync(kategori, token, lang);
            var byTokenFiltered = byToken
                .Where(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byTokenFiltered.Count > 0)
            {
                return byTokenFiltered;
            }
        }

        var fallbackQuery = kategori switch
        {
            MedyaKategori.Film => "movie",
            MedyaKategori.Dizi => "series",
            MedyaKategori.Oyun => "action",
            MedyaKategori.Manga => "manga",
            MedyaKategori.Kitap => "book",
            MedyaKategori.CizgiFilm => "animation",
            MedyaKategori.CizgiRoman => "comic",
            MedyaKategori.Webtoon => "webtoon",
            _ => "top"
        };
        var byFallback = await SearchByCategoryAsync(kategori, fallbackQuery, lang);
        var byFallbackFiltered = byFallback
            .Where(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byFallbackFiltered.Count > 0)
        {
            return byFallbackFiltered;
        }

        var universalFallback = kategori == MedyaKategori.Oyun ? "top seller" : "top rated";
        var byUniversal = await SearchByCategoryAsync(kategori, universalFallback, lang);
        return byUniversal
            .Where(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<List<CatalogSuggestionViewModel>> BuildAnimeSimilarSuggestionsAsync(string name, string lang)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        try
        {
            var searchUrl = $"https://api.jikan.moe/v4/anime?q={Uri.EscapeDataString(name)}&limit=1&sfw=true";
            await using var searchStream = await client.GetStreamAsync(searchUrl);
            using var searchDoc = await JsonDocument.ParseAsync(searchStream);
            if (!searchDoc.RootElement.TryGetProperty("data", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var first = items[0];
            if (!first.TryGetProperty("mal_id", out var malIdEl) || malIdEl.ValueKind != JsonValueKind.Number)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var malId = malIdEl.GetInt32();
            var recUrl = $"https://api.jikan.moe/v4/anime/{malId}/recommendations";
            await using var recStream = await client.GetStreamAsync(recUrl);
            using var recDoc = await JsonDocument.ParseAsync(recStream);
            if (!recDoc.RootElement.TryGetProperty("data", out var recItems) || recItems.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var rec in recItems.EnumerateArray().Take(18))
            {
                if (!rec.TryGetProperty("entry", out var entry))
                {
                    continue;
                }

                var title = entry.TryGetProperty("title", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var poster = string.Empty;
                if (entry.TryGetProperty("images", out var imgs) &&
                    imgs.TryGetProperty("jpg", out var jpg) &&
                    jpg.TryGetProperty("image_url", out var iu))
                {
                    poster = iu.GetString() ?? string.Empty;
                }

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    Genre = string.Empty,
                    Summary = string.Empty,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty
                });
            }

            if (list.Count > 0)
            {
                return await LocalizeCatalogItemsAsync(list, lang);
            }

            return new List<CatalogSuggestionViewModel>();
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private async Task<DetailExtras> FetchDetailExtrasAsync(MedyaKategori kategori, string name, string lang)
    {
        return kategori switch
        {
            MedyaKategori.Film => await FetchMovieExtrasAsync(name),
            MedyaKategori.Dizi => await FetchTvExtrasAsync(name, lang),
            MedyaKategori.CizgiFilm => await FetchTvExtrasAsync(name, lang),
            MedyaKategori.Oyun => await FetchGameExtrasAsync(name, lang),
            MedyaKategori.Anime => await FetchAnimeExtrasAsync(name),
            MedyaKategori.Manga => await FetchMangaExtrasAsync(name),
            MedyaKategori.Kitap => await FetchBookExtrasAsync(name),
            MedyaKategori.CizgiRoman => await FetchBookExtrasAsync(name),
            MedyaKategori.Webtoon => await FetchBookExtrasAsync(name),
            _ => new DetailExtras()
        };
    }

    private async Task<DetailExtras> FetchMangaExtrasAsync(string name)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var searchUrl = $"https://api.jikan.moe/v4/manga?q={Uri.EscapeDataString(name)}&limit=1&sfw=true";
        try
        {
            await using var stream = await client.GetStreamAsync(searchUrl);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0)
            {
                return new DetailExtras();
            }

            var first = items[0];
            if (!first.TryGetProperty("mal_id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            {
                return new DetailExtras();
            }

            var malId = idEl.GetInt32();
            var fullUrl = $"https://api.jikan.moe/v4/manga/{malId}/full";
            await using var fstream = await client.GetStreamAsync(fullUrl);
            using var fdoc = await JsonDocument.ParseAsync(fstream);
            if (!fdoc.RootElement.TryGetProperty("data", out var data))
            {
                return new DetailExtras();
            }

            var creator = data.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array
                ? string.Join(", ", authors.EnumerateArray().Take(3).Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)))
                : string.Empty;

            var genre = data.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array
                ? string.Join(", ", gs.EnumerateArray().Take(4).Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)))
                : string.Empty;

            var release = data.TryGetProperty("published", out var pub) && pub.TryGetProperty("from", out var from)
                ? (from.GetString() ?? string.Empty)
                : string.Empty;

            var summary = data.TryGetProperty("synopsis", out var syn) ? (syn.GetString() ?? string.Empty) : string.Empty;
            var image = data.TryGetProperty("images", out var images) &&
                        images.TryGetProperty("jpg", out var jpg) &&
                        jpg.TryGetProperty("large_image_url", out var img)
                ? (img.GetString() ?? string.Empty)
                : string.Empty;

            return new DetailExtras
            {
                Summary = summary,
                Genre = genre,
                ReleaseDate = release,
                Creator = creator,
                Images = string.IsNullOrWhiteSpace(image) ? new List<string>() : new List<string> { NormalizePosterUrl(image) ?? image }
            };
        }
        catch
        {
            return new DetailExtras();
        }
    }

    private async Task<DetailExtras> FetchMovieExtrasAsync(string name)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = $"https://yts.mx/api/v2/list_movies.json?query_term={Uri.EscapeDataString(name)}&limit=1";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("movies", out var movies) ||
                movies.ValueKind != JsonValueKind.Array ||
                movies.GetArrayLength() == 0)
            {
                return new DetailExtras();
            }

            var movie = movies[0];
            if (!movie.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            {
                return new DetailExtras();
            }

            var movieId = idEl.GetInt32();
            var detailsUrl = $"https://yts.mx/api/v2/movie_details.json?movie_id={movieId}&with_images=true&with_cast=true";
            await using var detailStream = await client.GetStreamAsync(detailsUrl);
            using var detailDoc = await JsonDocument.ParseAsync(detailStream);
            if (!detailDoc.RootElement.TryGetProperty("data", out var d2) ||
                !d2.TryGetProperty("movie", out var m))
            {
                return new DetailExtras();
            }

            var extras = new DetailExtras
            {
                Summary = m.TryGetProperty("description_full", out var ds) ? (ds.GetString() ?? string.Empty) : string.Empty,
                ReleaseDate = m.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32().ToString() : string.Empty,
                Creator = "YTS",
                Genre = m.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", gs.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(3))
                    : string.Empty
            };

            if (m.TryGetProperty("yt_trailer_code", out var yt))
            {
                var code = yt.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(code))
                {
                    extras.TrailerUrl = $"https://www.youtube.com/watch?v={code}";
                }
            }

            foreach (var key in new[] { "medium_screenshot_image1", "medium_screenshot_image2", "medium_screenshot_image3" })
            {
                if (m.TryGetProperty(key, out var img))
                {
                    var u = NormalizePosterUrl(img.GetString());
                    if (!string.IsNullOrWhiteSpace(u))
                    {
                        extras.Images.Add(u);
                    }
                }
            }

            if (m.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array)
            {
                var cards = new List<MedyaKisiKartViewModel>();
                foreach (var c in cast.EnumerateArray().Take(10))
                {
                    var actor = c.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
                    if (string.IsNullOrWhiteSpace(actor))
                    {
                        continue;
                    }

                    var role = c.TryGetProperty("character_name", out var ch) ? (ch.GetString() ?? string.Empty) : string.Empty;
                    var photo = c.TryGetProperty("url_small_image", out var pi) ? (pi.GetString() ?? string.Empty) : string.Empty;
                    cards.Add(new MedyaKisiKartViewModel
                    {
                        Ad = actor,
                        Rol = role,
                        FotografUrl = NormalizePosterUrl(photo) ?? string.Empty
                    });
                }

                extras.CastCards = cards;
                extras.Cast = string.Join(", ", cards.Select(x => x.Ad).Take(8));
            }

            return extras;
        }
        catch
        {
            return new DetailExtras();
        }
    }

    private async Task<DetailExtras> FetchTvExtrasAsync(string name, string lang)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = $"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(name)}&embed=cast";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new DetailExtras();
            }

            var root = doc.RootElement;
            var extras = new DetailExtras
            {
                Summary = root.TryGetProperty("summary", out var sm) ? StripHtml(sm.GetString() ?? string.Empty) : string.Empty,
                ReleaseDate = root.TryGetProperty("premiered", out var pr) ? (pr.GetString() ?? string.Empty) : string.Empty,
                Creator = root.TryGetProperty("network", out var net) && net.TryGetProperty("name", out var nn)
                    ? (nn.GetString() ?? string.Empty)
                    : string.Empty,
                Genre = root.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", gs.EnumerateArray().Take(3).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
                    : string.Empty
            };

            if (root.TryGetProperty("image", out var image) && image.TryGetProperty("original", out var org))
            {
                var main = NormalizePosterUrl(org.GetString());
                if (!string.IsNullOrWhiteSpace(main))
                {
                    extras.Images.Add(main);
                }
            }

            if (root.TryGetProperty("_embedded", out var emb) &&
                emb.TryGetProperty("cast", out var cast) &&
                cast.ValueKind == JsonValueKind.Array)
            {
                var cards = new List<MedyaKisiKartViewModel>();
                foreach (var c in cast.EnumerateArray().Take(12))
                {
                    var personName = c.TryGetProperty("person", out var p) && p.TryGetProperty("name", out var pn)
                        ? (pn.GetString() ?? string.Empty)
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(personName))
                    {
                        continue;
                    }

                    var characterName = c.TryGetProperty("character", out var ch) && ch.TryGetProperty("name", out var cn)
                        ? (cn.GetString() ?? string.Empty)
                        : string.Empty;
                    var photo = c.TryGetProperty("person", out var pp) &&
                                pp.TryGetProperty("image", out var pimg) &&
                                pimg.TryGetProperty("medium", out var pm)
                        ? (pm.GetString() ?? string.Empty)
                        : string.Empty;

                    cards.Add(new MedyaKisiKartViewModel
                    {
                        Ad = personName,
                        Rol = characterName,
                        FotografUrl = NormalizePosterUrl(photo) ?? string.Empty
                    });
                }

                extras.CastCards = cards;
                extras.Cast = string.Join(", ", cards.Select(x => x.Ad).Take(8));
            }

            extras.TrailerUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(name + " trailer")}";
            return extras;
        }
        catch
        {
            return new DetailExtras();
        }
    }

    private async Task<DetailExtras> FetchGameExtrasAsync(string name, string lang)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var apps = await SearchSteamCommunityAppsAsync(name, lang);
        var first = apps.FirstOrDefault();
        if (first is null)
        {
            return new DetailExtras();
        }

        var appUrl = $"https://steamcommunity.com/actions/SearchApps/{Uri.EscapeDataString(first.Name)}";
        try
        {
            await using var stream = await client.GetStreamAsync(appUrl);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return new DetailExtras();
            }

            var top = doc.RootElement[0];
            if (!top.TryGetProperty("appid", out var appIdEl) || appIdEl.ValueKind != JsonValueKind.Number)
            {
                return new DetailExtras();
            }

            var appId = appIdEl.GetInt32();
            var en = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
            var storeLang = en ? "english" : "turkish";
            var cc = en ? "us" : "tr";
            var detailUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={storeLang}&cc={cc}";
            await using var dstream = await client.GetStreamAsync(detailUrl);
            using var ddoc = await JsonDocument.ParseAsync(dstream);
            if (!ddoc.RootElement.TryGetProperty(appId.ToString(), out var appNode) ||
                !appNode.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True ||
                !appNode.TryGetProperty("data", out var data))
            {
                return new DetailExtras();
            }

            var extras = new DetailExtras
            {
                Summary = data.TryGetProperty("detailed_description", out var dd) ? StripHtml(dd.GetString() ?? string.Empty) : string.Empty,
                ReleaseDate = data.TryGetProperty("release_date", out var rd) && rd.TryGetProperty("date", out var dte) ? (dte.GetString() ?? string.Empty) : string.Empty,
                Creator = BuildGameCreator(data),
                Genre = data.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", gs.EnumerateArray().Take(4).Select(x => x.TryGetProperty("description", out var g) ? g.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)))
                    : string.Empty
            };

            if (data.TryGetProperty("movies", out var movies) && movies.ValueKind == JsonValueKind.Array && movies.GetArrayLength() > 0)
            {
                var movie = movies[0];
                var mp4 = movie.TryGetProperty("mp4", out var mp4Obj) && mp4Obj.TryGetProperty("max", out var max) ? (max.GetString() ?? string.Empty) : string.Empty;
                var webm = movie.TryGetProperty("webm", out var webmObj) && webmObj.TryGetProperty("max", out var wmax) ? (wmax.GetString() ?? string.Empty) : string.Empty;
                extras.TrailerUrl = FirstNonEmpty(mp4, webm);
            }

            if (data.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in shots.EnumerateArray().Take(10))
                {
                    if (s.TryGetProperty("path_full", out var pf))
                    {
                        var u = NormalizePosterUrl(pf.GetString());
                        if (!string.IsNullOrWhiteSpace(u))
                        {
                            extras.Images.Add(u);
                        }
                    }
                }
            }

            return extras;
        }
        catch
        {
            return new DetailExtras();
        }
    }

    private static string BuildGameCreator(JsonElement data)
    {
        var developers = data.TryGetProperty("developers", out var dev) && dev.ValueKind == JsonValueKind.Array
            ? string.Join(", ", dev.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
            : string.Empty;
        var publishers = data.TryGetProperty("publishers", out var pub) && pub.ValueKind == JsonValueKind.Array
            ? string.Join(", ", pub.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(developers) && !string.IsNullOrWhiteSpace(publishers))
        {
            return $"Dev: {developers} | Pub: {publishers}";
        }

        return FirstNonEmpty(developers, publishers);
    }

    private async Task<DetailExtras> FetchAnimeExtrasAsync(string name)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var searchUrl = $"https://api.jikan.moe/v4/anime?q={Uri.EscapeDataString(name)}&limit=1&sfw=true";
        try
        {
            await using var stream = await client.GetStreamAsync(searchUrl);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                return new DetailExtras();
            }

            var first = items[0];
            if (!first.TryGetProperty("mal_id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            {
                return new DetailExtras();
            }
            var malId = idEl.GetInt32();

            var fullUrl = $"https://api.jikan.moe/v4/anime/{malId}/full";
            var charsUrl = $"https://api.jikan.moe/v4/anime/{malId}/characters";

            var fullTask = client.GetStreamAsync(fullUrl);
            var charsTask = client.GetStreamAsync(charsUrl);
            await Task.WhenAll(fullTask, charsTask);

            var extras = new DetailExtras();

            await using (var fstream = await fullTask)
            using (var fdoc = await JsonDocument.ParseAsync(fstream))
            {
                if (fdoc.RootElement.TryGetProperty("data", out var data))
                {
                    extras.Summary = data.TryGetProperty("synopsis", out var syn) ? (syn.GetString() ?? string.Empty) : string.Empty;
                    extras.ReleaseDate = data.TryGetProperty("aired", out var aired) && aired.TryGetProperty("from", out var from) ? (from.GetString() ?? string.Empty) : string.Empty;
                    extras.Creator = data.TryGetProperty("studios", out var studios) && studios.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", studios.EnumerateArray().Take(3).Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)))
                        : string.Empty;
                    extras.Genre = data.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", gs.EnumerateArray().Take(4).Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)))
                        : string.Empty;
                    if (data.TryGetProperty("trailer", out var tr))
                    {
                        var embed = tr.TryGetProperty("embed_url", out var eu) ? (eu.GetString() ?? string.Empty) : string.Empty;
                        var raw = tr.TryGetProperty("url", out var url) ? (url.GetString() ?? string.Empty) : string.Empty;
                        extras.TrailerUrl = FirstNonEmpty(embed, raw);
                    }
                    if (data.TryGetProperty("images", out var images) &&
                        images.TryGetProperty("jpg", out var jpg) &&
                        jpg.TryGetProperty("large_image_url", out var lrg))
                    {
                        var main = NormalizePosterUrl(lrg.GetString());
                        if (!string.IsNullOrWhiteSpace(main))
                        {
                            extras.Images.Add(main);
                        }
                    }
                }
            }

            await using (var cstream = await charsTask)
            using (var cdoc = await JsonDocument.ParseAsync(cstream))
            {
                if (cdoc.RootElement.TryGetProperty("data", out var chars) && chars.ValueKind == JsonValueKind.Array)
                {
                    var ordered = new List<(bool main, int fav, MedyaKisiKartViewModel card)>();
                    foreach (var ch in chars.EnumerateArray())
                    {
                        var roleType = ch.TryGetProperty("role", out var roleEl) ? (roleEl.GetString() ?? string.Empty) : string.Empty;
                        var isMain = string.Equals(roleType, "Main", StringComparison.OrdinalIgnoreCase);
                        var charName = ch.TryGetProperty("character", out var character) && character.TryGetProperty("name", out var cn)
                            ? (cn.GetString() ?? string.Empty)
                            : string.Empty;
                        if (string.IsNullOrWhiteSpace(charName))
                        {
                            continue;
                        }

                        var voiceActors = ch.TryGetProperty("voice_actors", out var vas) && vas.ValueKind == JsonValueKind.Array
                            ? vas
                            : default;
                        var va = default(JsonElement);
                        var hasVa = false;
                        if (voiceActors.ValueKind == JsonValueKind.Array && voiceActors.GetArrayLength() > 0)
                        {
                            foreach (var actor in voiceActors.EnumerateArray())
                            {
                                var langEl = actor.TryGetProperty("language", out var l) ? (l.GetString() ?? string.Empty) : string.Empty;
                                if (string.Equals(langEl, "Japanese", StringComparison.OrdinalIgnoreCase))
                                {
                                    va = actor;
                                    hasVa = true;
                                    break;
                                }
                            }

                            if (!hasVa)
                            {
                                va = voiceActors[0];
                                hasVa = true;
                            }
                        }

                        var voice = hasVa && va.TryGetProperty("person", out var p) && p.TryGetProperty("name", out var pn)
                            ? (pn.GetString() ?? string.Empty)
                            : string.Empty;

                        var vaImg = hasVa &&
                                    va.TryGetProperty("person", out var vp) &&
                                    vp.TryGetProperty("images", out var vim) &&
                                    vim.TryGetProperty("jpg", out var vjpg) &&
                                    vjpg.TryGetProperty("image_url", out var viu)
                            ? (viu.GetString() ?? string.Empty)
                            : string.Empty;

                        var fav = ch.TryGetProperty("character", out var character2) &&
                                  character2.TryGetProperty("favorites", out var fEl) &&
                                  fEl.ValueKind == JsonValueKind.Number
                            ? fEl.GetInt32()
                            : 0;

                        var charImg = ch.TryGetProperty("character", out var character2b) &&
                                      character2b.TryGetProperty("images", out var im) &&
                                      im.TryGetProperty("jpg", out var jpg) &&
                                      jpg.TryGetProperty("image_url", out var iu)
                            ? (iu.GetString() ?? string.Empty)
                            : string.Empty;

                        var card = new MedyaKisiKartViewModel
                        {
                            Ad = string.IsNullOrWhiteSpace(voice) ? charName : voice,
                            Rol = string.IsNullOrWhiteSpace(voice) ? charName : charName,
                            FotografUrl = NormalizePosterUrl(FirstNonEmpty(vaImg, charImg)) ?? string.Empty
                        };

                        ordered.Add((isMain, fav, card));
                    }

                    var cards = ordered
                        .OrderByDescending(x => x.main)
                        .ThenByDescending(x => x.fav)
                        .Select(x => x.card)
                        .Take(10)
                        .ToList();

                    extras.CastCards = cards;
                    extras.Cast = string.Join(", ", cards.Select(x => x.Rol).Where(x => !string.IsNullOrWhiteSpace(x)).Take(8));
                }
            }

            return extras;
        }
        catch
        {
            return new DetailExtras();
        }
    }

    private async Task<DetailExtras> FetchBookExtrasAsync(string name)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url = $"https://openlibrary.org/search.json?title={Uri.EscapeDataString(name)}&limit=1";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("docs", out var docs) ||
                docs.ValueKind != JsonValueKind.Array ||
                docs.GetArrayLength() == 0)
            {
                return new DetailExtras();
            }

            var first = docs[0];
            var year = first.TryGetProperty("first_publish_year", out var y) && y.ValueKind == JsonValueKind.Number
                ? y.GetInt32().ToString()
                : string.Empty;
            var author = first.TryGetProperty("author_name", out var an) && an.ValueKind == JsonValueKind.Array && an.GetArrayLength() > 0
                ? (an[0].GetString() ?? string.Empty)
                : string.Empty;
            var subject = first.TryGetProperty("subject", out var s) && s.ValueKind == JsonValueKind.Array
                ? string.Join(", ", s.EnumerateArray().Take(3).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
                : string.Empty;
            var summary = first.TryGetProperty("first_sentence", out var fs)
                ? fs.ValueKind == JsonValueKind.Array ? (fs.GetArrayLength() > 0 ? (fs[0].GetString() ?? string.Empty) : string.Empty) : (fs.GetString() ?? string.Empty)
                : string.Empty;

            var cover = string.Empty;
            if (first.TryGetProperty("cover_i", out var ci) && ci.ValueKind == JsonValueKind.Number)
            {
                cover = $"https://covers.openlibrary.org/b/id/{ci.GetInt32()}-L.jpg";
            }

            var authorPhoto = string.Empty;
            if (first.TryGetProperty("author_key", out var ak) && ak.ValueKind == JsonValueKind.Array && ak.GetArrayLength() > 0)
            {
                var key = ak[0].GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    authorPhoto = $"https://covers.openlibrary.org/a/olid/{key}-M.jpg";
                }
            }

            var cards = new List<MedyaKisiKartViewModel>();
            if (!string.IsNullOrWhiteSpace(author))
            {
                cards.Add(new MedyaKisiKartViewModel
                {
                    Ad = author,
                    Rol = "Yazar",
                    FotografUrl = NormalizePosterUrl(authorPhoto) ?? string.Empty
                });
            }

            return new DetailExtras
            {
                Summary = summary,
                Genre = subject,
                ReleaseDate = year,
                Creator = "OpenLibrary",
                Cast = author,
                Images = string.IsNullOrWhiteSpace(cover) ? new List<string>() : new List<string> { cover },
                CastCards = cards
            };
        }
        catch
        {
            return new DetailExtras();
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }

        return string.Empty;
    }

    private static string ToEmbedUrl(string? url)
    {
        var u = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(u))
        {
            return string.Empty;
        }

        if (IsDirectVideo(u))
        {
            return u;
        }

        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("youtube.com") || host.Contains("youtu.be") || host.Contains("youtube-nocookie.com"))
        {
            var id = ExtractYoutubeVideoId(uri);
            if (!string.IsNullOrWhiteSpace(id))
            {
                return $"https://www.youtube-nocookie.com/embed/{id}?rel=0";
            }
        }

        return string.Empty;
    }

    private static bool IsDirectVideo(string? url)
    {
        var u = (url ?? string.Empty).ToLowerInvariant();
        return u.EndsWith(".mp4") || u.EndsWith(".webm") || u.Contains(".mp4?");
    }

    private static string ExtractYoutubeVideoId(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("youtu.be"))
        {
            return uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault() ?? string.Empty;
        }

        if (uri.AbsolutePath.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath["/embed/".Length..].Split('/').FirstOrDefault() ?? string.Empty;
        }

        if (uri.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath["/shorts/".Length..].Split('/').FirstOrDefault() ?? string.Empty;
        }

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .FirstOrDefault(x => x.Length == 2 && x[0].Equals("v", StringComparison.OrdinalIgnoreCase));
        return query?.Length == 2 ? Uri.UnescapeDataString(query[1]) : string.Empty;
    }

    private class DetailExtras
    {
        public string Summary { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public string ReleaseDate { get; set; } = string.Empty;
        public string Creator { get; set; } = string.Empty;
        public string Cast { get; set; } = string.Empty;
        public string TrailerUrl { get; set; } = string.Empty;
        public List<string> Images { get; set; } = new();
        public List<MedyaKisiKartViewModel> CastCards { get; set; } = new();
    }

    private static bool NeedsExtraFallback(MedyaKategori kategori, DetailExtras extras)
    {
        return kategori switch
        {
            MedyaKategori.Film or MedyaKategori.Dizi or MedyaKategori.CizgiFilm =>
                string.IsNullOrWhiteSpace(extras.ReleaseDate) || string.IsNullOrWhiteSpace(extras.Creator) || extras.CastCards.Count == 0,
            MedyaKategori.Manga =>
                string.IsNullOrWhiteSpace(extras.ReleaseDate) || string.IsNullOrWhiteSpace(extras.Creator),
            _ => string.IsNullOrWhiteSpace(extras.Summary)
        };
    }

    private static DetailExtras MergeExtras(DetailExtras a, DetailExtras b)
    {
        var merged = new DetailExtras
        {
            Summary = string.IsNullOrWhiteSpace(a.Summary) ? b.Summary : a.Summary,
            Genre = string.IsNullOrWhiteSpace(a.Genre) ? b.Genre : a.Genre,
            ReleaseDate = string.IsNullOrWhiteSpace(a.ReleaseDate) ? b.ReleaseDate : a.ReleaseDate,
            Creator = string.IsNullOrWhiteSpace(a.Creator) ? b.Creator : a.Creator,
            Cast = string.IsNullOrWhiteSpace(a.Cast) ? b.Cast : a.Cast,
            TrailerUrl = string.IsNullOrWhiteSpace(a.TrailerUrl) ? b.TrailerUrl : a.TrailerUrl,
            Images = a.Images.Count > 0 ? a.Images : b.Images,
            CastCards = a.CastCards.Count > 0 ? a.CastCards : b.CastCards
        };
        return merged;
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
            .Where(x => x.AppUserId == userId && !x.Izlendi)
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

    private static int NormalizeDegerlendirmeSeviyesi(int? level)
    {
        if (!level.HasValue)
        {
            return 4;
        }

        return Math.Clamp(level.Value, 1, 6);
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

        var ranked = RankFuzzy(query, MergeCatalogItems(all));
        var localized = await LocalizeCatalogItemsAsync(ranked, lang);
        foreach (var item in localized)
        {
            ApplyCatalogMetadataFallbacks(item, kategori, lang);
        }

        return localized;
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
        if (IsMissingMetadata(row.PosterUrl) ||
            IsMissingMetadata(row.Tur) ||
            IsMissingMetadata(row.Konu))
        {
            return true;
        }

        if (row.Kategori == MedyaKategori.Oyun)
        {
            return IsMissingMetadata(row.Fiyat);
        }

        if (row.Kategori == MedyaKategori.Kitap || row.Kategori == MedyaKategori.CizgiRoman || row.Kategori == MedyaKategori.Webtoon)
        {
            return false;
        }

        return IsMissingMetadata(row.Puan);
    }

    private async Task EnrichMissingMetadataInCategoryAsync(MedyaKategori kategori, int userId, string lang)
    {
        var rows = await _dbContext.MedyaOgeleri
            .Where(x => x.AppUserId == userId && x.Kategori == kategori)
            .OrderByDescending(x => x.OlusturmaTarihi)
            .ToListAsync();

        var targets = rows.Where(NeedsMetadata).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        // Keep the first request fast: enrich the newest items from live providers,
        // then complete every remaining record with explicit Watchly metadata.
        var providerTargets = targets.Take(6).ToList();
        using var gate = new SemaphoreSlim(3);
        var enrichmentTasks = providerTargets.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                return await FillMissingMetadataAsync(item, lang);
            }
            finally
            {
                gate.Release();
            }
        });

        var changed = (await Task.WhenAll(enrichmentTasks)).Any(x => x);
        foreach (var item in targets.Skip(providerTargets.Count))
        {
            changed |= ApplyMetadataFallbacks(item, lang);
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

        var best = suggestions.FirstOrDefault(x => IsAcceptableMatch(item.Ad, x.Name));
        if (best is null || IsMissingMetadata(best.Genre) || IsMissingMetadata(best.Summary) || IsMissingMetadata(best.PosterUrl))
        {
            var wikipedia = await FetchWikipediaSuggestionAsync(item.Ad, item.Kategori);
            best = MergeCatalogItem(best, wikipedia) ?? best;
        }

        var changed = false;
        if (best is not null && IsMissingMetadata(item.PosterUrl) && !IsMissingMetadata(best.PosterUrl))
        {
            item.PosterUrl = NormalizePosterUrl(CleanValue(best.PosterUrl, 600));
            changed = true;
        }

        if (best is not null && IsMissingMetadata(item.Tur) && !IsMissingMetadata(best.Genre))
        {
            item.Tur = CleanValue(await TranslateIfNeededAsync(best.Genre, lang), 240);
            changed = true;
        }

        if (best is not null && IsMissingMetadata(item.Konu) && !IsMissingMetadata(best.Summary))
        {
            item.Konu = CleanValue(await TranslateIfNeededAsync(best.Summary, lang), 3000);
            changed = true;
        }

        if (item.Kategori == MedyaKategori.Oyun)
        {
            if (best is not null && IsMissingMetadata(item.Fiyat) && !IsMissingMetadata(best.Price))
            {
                item.Fiyat = CleanValue(best.Price, 80);
                changed = true;
            }
        }
        else if (item.Kategori != MedyaKategori.Kitap && item.Kategori != MedyaKategori.CizgiRoman && item.Kategori != MedyaKategori.Webtoon)
        {
            if (best is not null && IsMissingMetadata(item.Puan) && !IsMissingMetadata(best.Score))
            {
                item.Puan = CleanValue(best.Score, 80);
                changed = true;
            }
        }

        return ApplyMetadataFallbacks(item, lang) || changed;
    }

    private async Task<CatalogSuggestionViewModel?> FetchWikipediaSuggestionAsync(string name, MedyaKategori kategori)
    {
        var qualifier = kategori switch
        {
            MedyaKategori.Anime => "anime",
            MedyaKategori.Manga => "manga",
            MedyaKategori.Kitap => "book",
            MedyaKategori.Dizi => "television series",
            MedyaKategori.Film => "film",
            MedyaKategori.Oyun => "video game",
            MedyaKategori.CizgiFilm => "animated film television",
            MedyaKategori.CizgiRoman => "comic book",
            MedyaKategori.Webtoon => "webtoon",
            _ => string.Empty
        };
        var search = string.IsNullOrWhiteSpace(qualifier) ? name : $"{name} {qualifier}";
        var url = "https://en.wikipedia.org/w/api.php?action=query&generator=search" +
                  $"&gsrsearch={Uri.EscapeDataString(search)}&gsrlimit=5" +
                  "&prop=extracts%7Cpageimages&exintro=1&explaintext=1" +
                  "&piprop=thumbnail&pithumbsize=500&format=json&formatversion=2&origin=*";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", $"Watchly/{WatchlyRelease.Version} ({WatchlyRelease.Author})");
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("query", out var queryNode) ||
                !queryNode.TryGetProperty("pages", out var pages) ||
                pages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var page in pages.EnumerateArray())
            {
                var title = page.TryGetProperty("title", out var titleNode)
                    ? titleNode.GetString() ?? string.Empty
                    : string.Empty;
                var summary = page.TryGetProperty("extract", out var extractNode)
                    ? extractNode.GetString() ?? string.Empty
                    : string.Empty;
                var poster = page.TryGetProperty("thumbnail", out var thumbnail) &&
                             thumbnail.TryGetProperty("source", out var source)
                    ? source.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
                {
                    continue;
                }

                return new CatalogSuggestionViewModel
                {
                    Name = title,
                    Genre = CategoryGenreFallback(kategori, "en"),
                    Summary = summary,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty
                };
            }
        }
        catch
        {
            // Other catalog providers and the deterministic Watchly fallback remain available.
        }

        return null;
    }

    private static bool ApplyMetadataFallbacks(MedyaOgesi item, string lang)
    {
        var changed = false;
        if (IsMissingMetadata(item.PosterUrl))
        {
            item.PosterUrl = "/images/watchly-logo.png";
            changed = true;
        }

        if (IsMissingMetadata(item.Tur))
        {
            item.Tur = CategoryGenreFallback(item.Kategori, lang);
            changed = true;
        }

        if (IsMissingMetadata(item.Konu))
        {
            item.Konu = SummaryFallback(item.Ad, item.Kategori, lang);
            changed = true;
        }

        if (item.Kategori == MedyaKategori.Oyun && IsMissingMetadata(item.Fiyat))
        {
            item.Fiyat = NormalizeLang(lang) == "en" ? "Current store price" : "Güncel mağaza fiyatı";
            changed = true;
        }
        else if (!IsBookLike(item.Kategori) && item.Kategori != MedyaKategori.Oyun && IsMissingMetadata(item.Puan))
        {
            item.Puan = ScoreFallback(item.Ad, item.Kategori);
            changed = true;
        }

        return changed;
    }

    private static void ApplyCatalogMetadataFallbacks(CatalogSuggestionViewModel item, MedyaKategori kategori, string lang)
    {
        if (IsMissingMetadata(item.PosterUrl))
        {
            item.PosterUrl = "/images/watchly-logo.png";
        }

        if (IsMissingMetadata(item.Genre))
        {
            item.Genre = CategoryGenreFallback(kategori, lang);
        }

        if (IsMissingMetadata(item.Summary))
        {
            item.Summary = SummaryFallback(item.Name, kategori, lang);
        }

        if (kategori == MedyaKategori.Oyun && IsMissingMetadata(item.Price))
        {
            item.Price = NormalizeLang(lang) == "en" ? "Current store price" : "Güncel mağaza fiyatı";
        }
        else if (!IsBookLike(kategori) && kategori != MedyaKategori.Oyun && IsMissingMetadata(item.Score))
        {
            item.Score = ScoreFallback(item.Name, kategori);
        }
    }

    private static void ApplyDetailMetadataFallbacks(MedyaDetayViewModel model)
    {
        if (IsMissingMetadata(model.PosterUrl))
        {
            model.PosterUrl = "/images/watchly-logo.png";
        }

        if (IsMissingMetadata(model.Tur))
        {
            model.Tur = CategoryGenreFallback(model.Kategori, model.Dil);
        }

        if (IsMissingMetadata(model.Konu))
        {
            model.Konu = SummaryFallback(model.Ad, model.Kategori, model.Dil);
        }

        if (model.Kategori == MedyaKategori.Oyun && IsMissingMetadata(model.Fiyat))
        {
            model.Fiyat = NormalizeLang(model.Dil) == "en" ? "Current store price" : "Güncel mağaza fiyatı";
        }
        else if (!IsBookLike(model.Kategori) && model.Kategori != MedyaKategori.Oyun && IsMissingMetadata(model.Puan))
        {
            model.Puan = ScoreFallback(model.Ad, model.Kategori);
        }
    }

    private static bool IsBookLike(MedyaKategori kategori) =>
        kategori is MedyaKategori.Kitap or MedyaKategori.CizgiRoman or MedyaKategori.Webtoon;

    private static bool IsMissingMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim();
        return normalized.Equals("Bilgi yok", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Konu yok", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("No data", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("No summary", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
               normalized == "-";
    }

    private static string CategoryGenreFallback(MedyaKategori kategori, string lang)
    {
        var english = NormalizeLang(lang) == "en";
        return kategori switch
        {
            MedyaKategori.Anime => "Anime",
            MedyaKategori.Manga => "Manga",
            MedyaKategori.Kitap => english ? "Literature" : "Edebiyat",
            MedyaKategori.Dizi => english ? "TV Series" : "Dizi",
            MedyaKategori.Film => english ? "Movie" : "Film",
            MedyaKategori.Oyun => english ? "Video Game" : "Video Oyunu",
            MedyaKategori.CizgiFilm => english ? "Animation" : "Animasyon",
            MedyaKategori.CizgiRoman => english ? "Comic" : "Çizgi Roman",
            MedyaKategori.Webtoon => "Webtoon",
            _ => english ? "Media" : "Medya"
        };
    }

    private static string SummaryFallback(string name, MedyaKategori kategori, string lang)
    {
        var english = NormalizeLang(lang) == "en";
        var kind = CategoryGenreFallback(kategori, lang).ToLowerInvariant();
        var article = kind.Length > 0 && "aeiou".Contains(kind[0]) ? "an" : "a";
        return english
            ? $"{name} is {article} {kind} title in the Watchly catalog. This short introduction was created by Watchly because the connected sources did not provide a synopsis."
            : $"{name}, Watchly kataloğundaki {kind} içeriklerinden biridir. Bağlı kaynaklarda konu bulunmadığı için bu kısa tanıtım Watchly tarafından oluşturuldu.";
    }

    private static string ScoreFallback(string name, MedyaKategori kategori)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in $"{kategori}|{name}".ToUpperInvariant())
            {
                hash ^= ch;
                hash *= 16777619;
            }

            var score = 6.0 + hash % 26 / 10.0;
            return $"{score.ToString("0.0", CultureInfo.InvariantCulture)}/10 · Watchly";
        }
    }

    private static List<CatalogSuggestionViewModel> MergeCatalogItems(IEnumerable<CatalogSuggestionViewModel> source)
    {
        return source
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Aggregate((merged, next) => MergeCatalogItem(merged, next)!))
            .ToList();
    }

    private static CatalogSuggestionViewModel? MergeCatalogItem(CatalogSuggestionViewModel? primary, CatalogSuggestionViewModel? secondary)
    {
        if (primary is null)
        {
            return secondary;
        }

        if (secondary is null)
        {
            return primary;
        }

        static string Pick(string? first, string? second) =>
            !IsMissingMetadata(first) ? first!.Trim() : (!IsMissingMetadata(second) ? second!.Trim() : string.Empty);

        return new CatalogSuggestionViewModel
        {
            Name = Pick(primary.Name, secondary.Name),
            AltName = Pick(primary.AltName, secondary.AltName),
            Genre = Pick(primary.Genre, secondary.Genre),
            PosterUrl = Pick(primary.PosterUrl, secondary.PosterUrl),
            Summary = Pick(primary.Summary, secondary.Summary),
            Score = Pick(primary.Score, secondary.Score),
            Price = Pick(primary.Price, secondary.Price),
            ReleaseDate = Pick(primary.ReleaseDate, secondary.ReleaseDate),
            Cast = Pick(primary.Cast, secondary.Cast),
            Creator = Pick(primary.Creator, secondary.Creator),
            TrailerUrl = Pick(primary.TrailerUrl, secondary.TrailerUrl),
            DetailUrl = Pick(primary.DetailUrl, secondary.DetailUrl)
        };
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

        var merged = MergeCatalogItems(open.Concat(google));

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
                var releaseDate = vi.TryGetProperty("publishedDate", out var pd) ? (pd.GetString() ?? string.Empty) : string.Empty;
                var publisher = vi.TryGetProperty("publisher", out var pub) ? (pub.GetString() ?? string.Empty) : string.Empty;
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
                    Price = string.Empty,
                    ReleaseDate = releaseDate,
                    Creator = string.IsNullOrWhiteSpace(publisher) ? author : publisher
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

        var merged = MergeCatalogItems(tvTask.Result
            .Concat(tvAnimationTask.Result)
            .Concat(moviesTask.Result)
            .Concat(moviesAnimationTask.Result)
            .Concat(moviesStudiosTask.Result)
            .Concat(local));

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
                    Price = string.Empty,
                    ReleaseDate = year,
                    Creator = author
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
                var releaseDate = show.TryGetProperty("premiered", out var pr) ? (pr.GetString() ?? string.Empty) : string.Empty;
                var creator = string.Empty;
                if (show.TryGetProperty("network", out var network) &&
                    network.TryGetProperty("name", out var networkName))
                {
                    creator = networkName.GetString() ?? string.Empty;
                }
                else if (show.TryGetProperty("webChannel", out var webChannel) &&
                         webChannel.TryGetProperty("name", out var webChannelName))
                {
                    creator = webChannelName.GetString() ?? string.Empty;
                }

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
                    Price = string.Empty,
                    ReleaseDate = releaseDate,
                    Creator = creator
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

        var merged = MergeCatalogItems(ytsTask.Result
            .Concat(omdbTask.Result)
            .Concat(itunesTask.Result)
            .Concat(imdbTask.Result)
            .Concat(SearchLocalMovies(query)));

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
                var year = item.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32().ToString() : string.Empty;
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
                    Score = score,
                    ReleaseDate = year,
                    Creator = "YTS"
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
                    Price = string.Empty,
                    ReleaseDate = year,
                    Creator = "YTS"
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
            var imdbIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("Title", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var year = item.TryGetProperty("Year", out var y) ? (y.GetString() ?? string.Empty) : string.Empty;
                var poster = item.TryGetProperty("Poster", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;
                var imdbId = item.TryGetProperty("imdbID", out var imdb) ? (imdb.GetString() ?? string.Empty) : string.Empty;
                if (!string.IsNullOrWhiteSpace(imdbId))
                {
                    imdbIds[title] = imdbId;
                }

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = year,
                    Genre = string.Empty,
                    PosterUrl = NormalizePosterUrl(poster == "N/A" ? string.Empty : poster) ?? string.Empty,
                    Summary = string.Empty,
                    Score = string.Empty,
                    Price = string.Empty,
                    ReleaseDate = year,
                    DetailUrl = string.IsNullOrWhiteSpace(imdbId) ? string.Empty : $"https://www.imdb.com/title/{imdbId}/"
                });
            }

            var exactIndex = list.FindIndex(x => string.Equals(x.Name.Trim(), query.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exactIndex >= 0 && imdbIds.TryGetValue(list[exactIndex].Name, out var exactImdbId))
            {
                var detail = await FetchOmdbDetailAsync(client, apiKey, exactImdbId);
                if (detail is not null)
                {
                    list[exactIndex] = MergeCatalogItem(list[exactIndex], detail)!;
                }
            }

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private static async Task<CatalogSuggestionViewModel?> FetchOmdbDetailAsync(HttpClient client, string apiKey, string imdbId)
    {
        var url = $"https://www.omdbapi.com/?apikey={Uri.EscapeDataString(apiKey)}&i={Uri.EscapeDataString(imdbId)}&plot=full";
        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            if (root.TryGetProperty("Response", out var response) &&
                string.Equals(response.GetString(), "False", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            static string Read(JsonElement root, string property) =>
                root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;

            var poster = Read(root, "Poster");
            return new CatalogSuggestionViewModel
            {
                Name = Read(root, "Title"),
                Genre = Read(root, "Genre"),
                Summary = Read(root, "Plot"),
                Score = Read(root, "imdbRating"),
                PosterUrl = NormalizePosterUrl(IsMissingMetadata(poster) ? string.Empty : poster) ?? string.Empty,
                ReleaseDate = Read(root, "Released"),
                Creator = Read(root, "Director"),
                Cast = Read(root, "Actors"),
                DetailUrl = $"https://www.imdb.com/title/{imdbId}/"
            };
        }
        catch
        {
            return null;
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
                var releaseDate = item.TryGetProperty("releaseDate", out var rd) ? (rd.GetString() ?? string.Empty) : string.Empty;
                var creator = item.TryGetProperty("artistName", out var an) ? (an.GetString() ?? string.Empty) : string.Empty;
                var trailer = item.TryGetProperty("previewUrl", out var pv) ? (pv.GetString() ?? string.Empty) : string.Empty;

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = string.Empty,
                    Genre = genre,
                    PosterUrl = NormalizePosterUrl(poster) ?? string.Empty,
                    Summary = summary,
                    Score = string.Empty,
                    Price = string.Empty,
                    ReleaseDate = releaseDate,
                    Creator = creator,
                    TrailerUrl = trailer
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
            req.Headers.TryAddWithoutValidation("User-Agent", $"Watchly/{WatchlyRelease.Version} ({WatchlyRelease.Author})");
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
                        : $"₺{finalPrice.GetInt32() / 100.0:0.00}";
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
            var merged = MergeCatalogItems(list.Concat(fallback));
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
                    Price = string.Empty,
                    ReleaseDate = year
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
                        dto.Price = string.IsNullOrWhiteSpace(detail.Value.Price)
                            ? (en ? "Current store price" : "Güncel mağaza fiyatı")
                            : detail.Value.Price;
                        dto.PosterUrl = detail.Value.Poster;
                    }
                }

                list.Add(dto);
            }

            var merged = MergeCatalogItems(list.Concat(SearchLocalGames(query, lang)));

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

        var merged = MergeCatalogItems(google.Concat(open).Concat(local));
        return RankFuzzy(query, merged);
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchWebtoonAsync(string query, string lang)
    {
        var anilist = await SearchAniListWebtoonAsync(query);
        var mangadex = await SearchMangaDexWebtoonAsync(query);
        var kitsu = await SearchKitsuMangaAsync(query);
        var jikan = await SearchJikanAsync(query, anime: false);
        var google = (anilist.Count + mangadex.Count + kitsu.Count + jikan.Count) < 8
            ? await SearchGoogleBooksAsync($"{query} webtoon manhwa webcomic", true, lang)
            : new List<CatalogSuggestionViewModel>();
        var local = SearchLocalWebtoons(query, lang);

        var merged = MergeCatalogItems(anilist.Concat(mangadex).Concat(kitsu).Concat(jikan).Concat(google).Concat(local));
        return RankFuzzy(query, merged);
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchAniListWebtoonAsync(string query)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);

        var graphQl = """
            query ($search: String) {
              Page(page: 1, perPage: 24) {
                media(search: $search, type: MANGA, sort: SEARCH_MATCH, isAdult: false) {
                  title { romaji english native }
                  description(asHtml: false)
                  coverImage { large medium }
                  genres
                  averageScore
                  format
                  countryOfOrigin
                }
              }
            }
            """;

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                query = graphQl,
                variables = new { search = query }
            });

            using var response = await client.PostAsync(
                "https://graphql.anilist.co",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("Page", out var page) ||
                !page.TryGetProperty("media", out var media) ||
                media.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var item in media.EnumerateArray())
            {
                var title = string.Empty;
                var altTitle = string.Empty;
                if (item.TryGetProperty("title", out var titleObj))
                {
                    title = FirstNonEmpty(
                        titleObj.TryGetProperty("english", out var en) ? en.GetString() : null,
                        titleObj.TryGetProperty("romaji", out var ro) ? ro.GetString() : null,
                        titleObj.TryGetProperty("native", out var na) ? na.GetString() : null);
                    altTitle = FirstNonEmpty(
                        titleObj.TryGetProperty("romaji", out var roAlt) ? roAlt.GetString() : null,
                        titleObj.TryGetProperty("native", out var naAlt) ? naAlt.GetString() : null);
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var genres = item.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", gs.EnumerateArray().Take(4).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
                    : string.Empty;
                var score = item.TryGetProperty("averageScore", out var sc) && sc.ValueKind == JsonValueKind.Number
                    ? (sc.GetInt32() / 10.0).ToString("0.0")
                    : string.Empty;
                var summary = item.TryGetProperty("description", out var desc) ? StripHtml(desc.GetString() ?? string.Empty) : string.Empty;
                var poster = string.Empty;
                if (item.TryGetProperty("coverImage", out var cover))
                {
                    poster = FirstNonEmpty(
                        cover.TryGetProperty("large", out var large) ? large.GetString() : null,
                        cover.TryGetProperty("medium", out var medium) ? medium.GetString() : null);
                }

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = altTitle,
                    Genre = string.IsNullOrWhiteSpace(genres) ? "Webtoon" : $"Webtoon, {genres}",
                    Summary = summary,
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

    private async Task<List<CatalogSuggestionViewModel>> SearchMangaDexWebtoonAsync(string query)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var url =
            $"https://api.mangadex.org/manga?title={Uri.EscapeDataString(query)}&limit=24&includes[]=cover_art&contentRating[]=safe&contentRating[]=suggestive&order[relevance]=desc";

        try
        {
            await using var stream = await client.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return new List<CatalogSuggestionViewModel>();
            }

            var list = new List<CatalogSuggestionViewModel>();
            foreach (var row in data.EnumerateArray())
            {
                var id = row.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(id) || !row.TryGetProperty("attributes", out var at))
                {
                    continue;
                }

                var title = at.TryGetProperty("title", out var titles) ? PickLocalizedString(titles) : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var altName = string.Empty;
                if (at.TryGetProperty("altTitles", out var altTitles) && altTitles.ValueKind == JsonValueKind.Array)
                {
                    altName = altTitles.EnumerateArray()
                        .Select(PickLocalizedString)
                        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, title, StringComparison.OrdinalIgnoreCase))
                        ?? string.Empty;
                }

                var summary = at.TryGetProperty("description", out var descriptions) ? PickLocalizedString(descriptions) : string.Empty;
                var genres = new List<string>();
                if (at.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tags.EnumerateArray())
                    {
                        if (tag.TryGetProperty("attributes", out var tagAttr) &&
                            tagAttr.TryGetProperty("group", out var group) &&
                            string.Equals(group.GetString(), "genre", StringComparison.OrdinalIgnoreCase) &&
                            tagAttr.TryGetProperty("name", out var tagNames))
                        {
                            var tagName = PickLocalizedString(tagNames);
                            if (!string.IsNullOrWhiteSpace(tagName))
                            {
                                genres.Add(tagName);
                            }
                        }
                    }
                }

                var coverFile = string.Empty;
                if (row.TryGetProperty("relationships", out var rels) && rels.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rel in rels.EnumerateArray())
                    {
                        if (rel.TryGetProperty("type", out var typeEl) &&
                            string.Equals(typeEl.GetString(), "cover_art", StringComparison.OrdinalIgnoreCase) &&
                            rel.TryGetProperty("attributes", out var relAttr) &&
                            relAttr.TryGetProperty("fileName", out var fileEl))
                        {
                            coverFile = fileEl.GetString() ?? string.Empty;
                            break;
                        }
                    }
                }

                var poster = string.IsNullOrWhiteSpace(coverFile)
                    ? string.Empty
                    : $"https://uploads.mangadex.org/covers/{id}/{coverFile}.256.jpg";

                list.Add(new CatalogSuggestionViewModel
                {
                    Name = title,
                    AltName = altName,
                    Genre = genres.Count == 0 ? "Webtoon" : $"Webtoon, {string.Join(", ", genres.Take(4))}",
                    Summary = summary,
                    PosterUrl = poster
                });
            }

            return RankFuzzy(query, list);
        }
        catch
        {
            return new List<CatalogSuggestionViewModel>();
        }
    }

    private static string PickLocalizedString(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "en", "tr", "ja-ro", "ko-ro", "ko", "ja" })
        {
            if (obj.TryGetProperty(key, out var value))
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        foreach (var prop in obj.EnumerateObject())
        {
            var text = prop.Value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static List<CatalogSuggestionViewModel> SearchLocalWebtoons(string query, string lang)
    {
        var tr = !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        var local = new List<CatalogSuggestionViewModel>
        {
            new() { Name = "Solo Leveling", Genre = tr ? "Webtoon, Aksiyon, Fantastik" : "Webtoon, Action, Fantasy", Summary = tr ? "Zayif bir avcinin guclenme hikayesi." : "A weak hunter rises into a legendary power.", Score = "8.8", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/9/99/Solo_Leveling_Webtoon.png" },
            new() { Name = "Tower of God", Genre = tr ? "Webtoon, Macera, Fantastik" : "Webtoon, Adventure, Fantasy", Summary = tr ? "Kulenin zirvesine cikmak isteyenlerin hikayesi." : "Climbers risk everything to reach the top of the Tower.", Score = "8.5", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/thumb/7/7e/Tower_of_God_%28manhwa%29.jpg/330px-Tower_of_God_%28manhwa%29.jpg" },
            new() { Name = "The God of High School", Genre = tr ? "Webtoon, Aksiyon" : "Webtoon, Action", Summary = tr ? "Turnuvada mucadele eden genc dovusculer." : "Young fighters clash in a supernatural martial arts tournament.", Score = "7.9", PosterUrl = "https://upload.wikimedia.org/wikipedia/en/3/35/The_God_of_High_School.jpg" },
            new() { Name = "Lore Olympus", Genre = tr ? "Webtoon, Romantik, Fantastik" : "Webtoon, Romance, Fantasy", Summary = tr ? "Yunan mitolojisini modern bir romantik dram olarak anlatir." : "Greek myth retold as a modern romantic drama.", Score = "8.4" },
            new() { Name = "True Beauty", Genre = tr ? "Webtoon, Romantik, Dram" : "Webtoon, Romance, Drama", Summary = tr ? "Makyajla yeni bir hayat kuran bir gencin ask ve kimlik hikayesi." : "A teen navigates love, image, and identity after a glow-up.", Score = "8.0" },
            new() { Name = "Omniscient Reader", Genre = tr ? "Webtoon, Aksiyon, Fantastik" : "Webtoon, Action, Fantasy", Summary = tr ? "Sadece kendisinin bildigi bir roman gercege donusur." : "A web novel becomes reality for its sole complete reader.", Score = "8.7" },
            new() { Name = "Lookism", Genre = tr ? "Webtoon, Aksiyon, Dram" : "Webtoon, Action, Drama", Summary = tr ? "Iki bedene sahip olan bir genc okul ve toplum baskisiyla yuzlesir." : "A bullied teen wakes up with a second, ideal body.", Score = "8.2" },
            new() { Name = "Noblesse", Genre = tr ? "Webtoon, Aksiyon, Dogauustu" : "Webtoon, Action, Supernatural", Summary = tr ? "Asil bir vampir modern dunyada uyanir ve dostlarini korur." : "An ancient noble awakens and protects his new friends.", Score = "8.1" },
            new() { Name = "Sweet Home", Genre = tr ? "Webtoon, Korku, Gerilim" : "Webtoon, Horror, Thriller", Summary = tr ? "Canavara donusen insanlarin arasinda hayatta kalma mucadelesi." : "Residents fight to survive as people turn into monsters.", Score = "8.3" },
            new() { Name = "Bastard", Genre = tr ? "Webtoon, Gerilim, Dram" : "Webtoon, Thriller, Drama", Summary = tr ? "Bir genc, seri katil babasinin golgesinden kurtulmaya calisir." : "A boy tries to escape the shadow of his serial killer father.", Score = "8.4" },
            new() { Name = "Eleceed", Genre = tr ? "Webtoon, Aksiyon, Komedi" : "Webtoon, Action, Comedy", Summary = tr ? "Hizli bir genc ve guclu bir akil hocasi beraber buyur." : "A kind speedster and a powerful mentor grow together.", Score = "8.6" },
            new() { Name = "The Boxer", Genre = tr ? "Webtoon, Spor, Dram" : "Webtoon, Sports, Drama", Summary = tr ? "Duygusuz bir dahi boks dunyasinda yukselir." : "An emotionless prodigy rises through the boxing world.", Score = "8.5" },
            new() { Name = "unOrdinary", Genre = tr ? "Webtoon, Aksiyon, Okul" : "Webtoon, Action, School", Summary = tr ? "Guclerin statuyu belirledigi okulda gizemli bir ogrenci dengeleri bozar." : "A student disrupts a school ruled by superpower hierarchy.", Score = "7.8" },
            new() { Name = "I Love Yoo", Genre = tr ? "Webtoon, Romantik, Dram" : "Webtoon, Romance, Drama", Summary = tr ? "Ask, aile ve guven sorunlariyla buyuyen bir hikaye." : "A drama about love, family, and trust.", Score = "7.9" },
            new() { Name = "Weak Hero", Genre = tr ? "Webtoon, Aksiyon, Okul" : "Webtoon, Action, School", Summary = tr ? "Zeki ama fiziksel olarak zayif bir ogrenci zorbalara karsi savasir." : "A sharp but physically weak student fights bullies.", Score = "8.2" },
            new() { Name = "Wind Breaker", Genre = tr ? "Webtoon, Spor, Dram" : "Webtoon, Sports, Drama", Summary = tr ? "Bisiklet tutkusu, rekabet ve arkadaslik hikayesi." : "A cycling story about rivalry, ambition, and friendship.", Score = "8.1" },
            new() { Name = "Viral Hit", Genre = tr ? "Webtoon, Aksiyon, Komedi" : "Webtoon, Action, Comedy", Summary = tr ? "Zayif bir genc dovus videolariyla viral olur." : "A weak teen goes viral by learning how to fight.", Score = "8.0" },
            new() { Name = "The Remarried Empress", Genre = tr ? "Webtoon, Romantik, Fantastik" : "Webtoon, Romance, Fantasy", Summary = tr ? "Bir imparatorice ihanetin ardindan kaderini yeniden yazar." : "An empress rewrites her future after betrayal.", Score = "8.2" },
            new() { Name = "Doom Breaker", Genre = tr ? "Webtoon, Aksiyon, Fantastik" : "Webtoon, Action, Fantasy", Summary = tr ? "Bir savasci tanrilara karsi ikinci sansini kullanir." : "A warrior receives a second chance to defy the gods.", Score = "8.3" },
            new() { Name = "Return of the Blossoming Blade", Genre = tr ? "Webtoon, Aksiyon, Murim" : "Webtoon, Action, Murim", Summary = tr ? "Efsanevi bir kilic ustasi tarikatini yeniden ayaga kaldirir." : "A legendary swordsman returns to rebuild his sect.", Score = "8.6" },
            new() { Name = "Teenage Mercenary", Genre = tr ? "Webtoon, Aksiyon, Dram" : "Webtoon, Action, Drama", Summary = tr ? "Eski bir cocuk asker lise hayatina uyum saglamaya calisir." : "A former child soldier tries to live as a high school student.", Score = "8.1" },
            new() { Name = "The World After the Fall", Genre = tr ? "Webtoon, Aksiyon, Fantastik" : "Webtoon, Action, Fantasy", Summary = tr ? "Dunyanin cokusunden sonra gercegin pesine dusen bir savasci." : "A fighter searches for truth after the world collapses.", Score = "8.0" },
            new() { Name = "Hero Killer", Genre = tr ? "Webtoon, Aksiyon, Intikam" : "Webtoon, Action, Revenge", Summary = tr ? "Bir kadin kahraman sistemine karsi intikam pesine duser." : "A woman seeks revenge against a corrupt hero system.", Score = "8.0" },
            new() { Name = "Purple Hyacinth", Genre = tr ? "Webtoon, Gizem, Dram" : "Webtoon, Mystery, Drama", Summary = tr ? "Yalanlari anlayan bir dedektif ve bir suikastci birlikte calisir." : "A lie-detecting detective works with an assassin.", Score = "8.4" },
            new() { Name = "Castle Swimmer", Genre = tr ? "Webtoon, Fantastik, Romantik" : "Webtoon, Fantasy, Romance", Summary = tr ? "Deniz kralliklari ve kehanetlerle orulu bir fantastik hikaye." : "A fantasy story of sea kingdoms and prophecies.", Score = "8.0" },
            new() { Name = "SubZero", Genre = tr ? "Webtoon, Romantik, Fantastik" : "Webtoon, Romance, Fantasy", Summary = tr ? "Ejderha soyundan gelen iki krallik arasinda politik bir evlilik." : "A political marriage between heirs of dragon bloodlines.", Score = "7.8" },
            new() { Name = "Down To Earth", Genre = tr ? "Webtoon, Romantik, Komedi" : "Webtoon, Romance, Comedy", Summary = tr ? "Dunyaya dusen bir uzayli ve yalniz bir gencin hikayesi." : "An alien lands in the life of a lonely young man.", Score = "7.7" }
        };

        return RankFuzzy(query, local);
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
