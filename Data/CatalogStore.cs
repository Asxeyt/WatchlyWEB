using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KategoriSecici.Models;
using KategoriSecici.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace KategoriSecici.Data;

public sealed class CatalogStore
{
    private const string IndexName = "watchly-works";
    private static readonly SemaphoreSlim IndexLock = new(1, 1);
    private static bool _indexReady;
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _config;
    private readonly ILogger<CatalogStore> _logger;

    public CatalogStore(AppDbContext db, IHttpClientFactory clients, IConfiguration config, ILogger<CatalogStore> logger)
    {
        _db = db;
        _clients = clients;
        _config = config;
        _logger = logger;
    }

    public async Task<List<CatalogSuggestionViewModel>> SearchAsync(MedyaKategori category, string query)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length < 1)
        {
            return new();
        }

        var meiliIds = await SearchMeiliAsync(category, q);
        if (meiliIds is { Count: > 0 })
        {
            var matches = await _db.CatalogWorks.AsNoTracking()
                .Where(x => x.Kategori == category && meiliIds.Contains(x.Id))
                .ToListAsync();
            var byId = matches.ToDictionary(x => x.Id);
            return meiliIds.Where(byId.ContainsKey).Select(id => ToSuggestion(byId[id])).ToList();
        }

        // A safe fallback while Meilisearch is unavailable or its index is warming up.
        var needle = q.ToLowerInvariant();
        var rows = await _db.CatalogWorks.AsNoTracking()
            .Where(x => x.Kategori == category &&
                (x.Ad.ToLower().Contains(needle) || x.AltName.ToLower().Contains(needle)))
            .OrderBy(x => x.Ad)
            .Take(100)
            .ToListAsync();
        return rows.OrderBy(x => x.Ad.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Ad).Take(20).Select(ToSuggestion).ToList();
    }

    public async Task UpsertAsync(MedyaKategori category, IEnumerable<CatalogSuggestionViewModel> suggestions)
    {
        foreach (var suggestion in suggestions.Take(60))
        {
            var ids = NormalizeIds(suggestion.ExternalIds);
            if (string.IsNullOrWhiteSpace(suggestion.Name) || ids.Count == 0)
            {
                continue; // Never invent an identity from title alone.
            }

            try
            {
                var matches = new List<CatalogWork>();
                foreach (var (source, value) in ids)
                {
                    var linked = await _db.CatalogExternalIds
                        .Include(x => x.CatalogWork).ThenInclude(x => x.ExternalIds)
                        .FirstOrDefaultAsync(x => x.Kategori == category && x.Source == source && x.ExternalId == value);
                    if (linked is not null && matches.All(x => x.Id != linked.CatalogWorkId))
                    {
                        matches.Add(linked.CatalogWork);
                    }
                }

                var normalizedTitle = NormalizeTitle(suggestion.Name);
                var year = ExtractYear(suggestion.ReleaseDate);
                var work = matches.FirstOrDefault();
                if (work is null && year is not null && category != MedyaKategori.Kitap)
                {
                    work = await _db.CatalogWorks.Include(x => x.ExternalIds)
                        .FirstOrDefaultAsync(x => x.Kategori == category &&
                            x.NormalizedTitle == normalizedTitle && x.ReleaseYear == year);
                }

                if (work is null)
                {
                    work = new CatalogWork
                    {
                        Kategori = category,
                        Ad = suggestion.Name.Trim()[..Math.Min(suggestion.Name.Trim().Length, 200)],
                        NormalizedTitle = normalizedTitle,
                        ReleaseYear = year
                    };
                    _db.CatalogWorks.Add(work);
                    await _db.SaveChangesAsync();
                }

                // Two provider records that share a verified ID become one catalog work.
                foreach (var duplicate in matches.Where(x => x.Id != work.Id))
                {
                    foreach (var external in duplicate.ExternalIds.ToList())
                    {
                        external.CatalogWorkId = work.Id;
                    }
                    _db.CatalogWorks.Remove(duplicate);
                }

                MergeMetadata(work, suggestion);
                foreach (var (source, value) in ids)
                {
                    if (work.ExternalIds.All(x => x.Source != source || x.ExternalId != value) &&
                        !await _db.CatalogExternalIds.AnyAsync(x => x.Kategori == category && x.Source == source && x.ExternalId == value))
                    {
                        work.ExternalIds.Add(new CatalogExternalId { Kategori = category, Source = source, ExternalId = value });
                    }
                }

                await _db.SaveChangesAsync();
                suggestion.CatalogId = work.Id;
                await IndexAsync(work);
            }
            catch (DbUpdateException ex)
            {
                // A concurrent request may win the unique-ID race; the next search reads its row.
                _logger.LogWarning(ex, "Catalog identity collision for {Category}: {Title}", category, suggestion.Name);
                _db.ChangeTracker.Clear();
            }
        }
    }

    private static void MergeMetadata(CatalogWork work, CatalogSuggestionViewModel item)
    {
        static string Pick(string current, string incoming, int max) =>
            string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(incoming)
                ? incoming.Trim()[..Math.Min(incoming.Trim().Length, max)]
                : current;

        work.AltName = Pick(work.AltName, item.AltName, 200);
        work.PosterUrl = Pick(work.PosterUrl, item.PosterUrl, 600);
        work.Tur = Pick(work.Tur, item.Genre, 240);
        if (!string.IsNullOrWhiteSpace(item.Summary) &&
            (string.IsNullOrWhiteSpace(work.Konu) || item.Summary.Length > work.Konu.Length + 40))
        {
            work.Konu = item.Summary.Trim()[..Math.Min(item.Summary.Trim().Length, 3000)];
        }
        work.Puan = Pick(work.Puan, item.Score, 80);
        work.Fiyat = Pick(work.Fiyat, item.Price, 80);
        work.YayinTarihi = Pick(work.YayinTarihi, item.ReleaseDate, 80);
        work.Yapimci = Pick(work.Yapimci, item.Creator, 240);
        work.UpdatedAt = DateTime.UtcNow;
    }

    private static CatalogSuggestionViewModel ToSuggestion(CatalogWork row) => new()
    {
        CatalogId = row.Id,
        Name = row.Ad,
        AltName = row.AltName,
        PosterUrl = row.PosterUrl,
        Genre = row.Tur,
        Summary = row.Konu,
        Score = row.Puan,
        Price = row.Fiyat,
        ReleaseDate = row.YayinTarihi,
        Creator = row.Yapimci
    };

    private static Dictionary<string, string> NormalizeIds(Dictionary<string, string> raw)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawSource, rawValue) in raw)
        {
            var source = rawSource.Trim().ToLowerInvariant();
            var value = rawValue.Trim();
            if (source == "isbn") value = NormalizeIsbn(value);
            if (source == "imdb") value = value.ToLowerInvariant();
            if (source.Length is < 1 or > 40 || value.Length is < 1 or > 100) continue;
            ids[source] = value;
        }
        return ids;
    }

    public static string NormalizeIsbn(string raw)
    {
        var isbn = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (isbn.Length == 13 && isbn.All(char.IsDigit))
        {
            var sum13 = isbn[..12].Select((ch, i) => (ch - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
            return (10 - sum13 % 10) % 10 == isbn[12] - '0' ? isbn : string.Empty;
        }
        if (isbn.Length != 10 || !isbn[..9].All(char.IsDigit) ||
            !(char.IsDigit(isbn[9]) || isbn[9] == 'X')) return string.Empty;
        var sum10 = isbn.Select((ch, i) => (10 - i) * (ch == 'X' ? 10 : ch - '0')).Sum();
        if (sum10 % 11 != 0) return string.Empty;
        var first12 = "978" + isbn[..9];
        var sum = first12.Select((ch, i) => (ch - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return first12 + ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizeTitle(string name)
    {
        var decomposed = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var chars = decomposed.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(ch));
        return new string(chars.Take(200).ToArray());
    }

    private static int? ExtractYear(string value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\b(?:18|19|20|21)\d{2}\b");
        return match.Success && int.TryParse(match.Value, out var year) ? year : null;
    }

    private string? MeiliUrl => _config["MEILI_URL"]?.TrimEnd('/');
    private string? MeiliKey => _config["MEILI_API_KEY"];

    private HttpClient? MeiliClient()
    {
        if (string.IsNullOrWhiteSpace(MeiliUrl) || string.IsNullOrWhiteSpace(MeiliKey)) return null;
        if (!Uri.TryCreate(MeiliUrl + "/", UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)) return null;
        var client = _clients.CreateClient();
        client.BaseAddress = baseUri;
        client.Timeout = TimeSpan.FromSeconds(3);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MeiliKey);
        return client;
    }

    private async Task<bool> EnsureIndexAsync(HttpClient client)
    {
        if (_indexReady) return true;
        await IndexLock.WaitAsync();
        try
        {
            if (_indexReady) return true;
            using var existing = await client.GetAsync($"indexes/{IndexName}");
            if (existing.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                using var created = await client.PostAsJsonAsync("indexes", new { uid = IndexName, primaryKey = "id" });
                if (!created.IsSuccessStatusCode && created.StatusCode != System.Net.HttpStatusCode.Conflict) return false;
            }
            else if (!existing.IsSuccessStatusCode) return false;
            using var settings = await client.PatchAsJsonAsync($"indexes/{IndexName}/settings", new { filterableAttributes = new[] { "category" } });
            _indexReady = settings.IsSuccessStatusCode;
            return _indexReady;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meilisearch is unavailable; using database search");
            return false;
        }
        finally
        {
            IndexLock.Release();
        }
    }

    private async Task<List<int>?> SearchMeiliAsync(MedyaKategori category, string query)
    {
        using var client = MeiliClient();
        if (client is null || !await EnsureIndexAsync(client)) return null;
        try
        {
            using var response = await client.PostAsJsonAsync($"indexes/{IndexName}/search", new
            {
                q = query,
                filter = $"category = {(int)category}",
                limit = 12
            });
            if (!response.IsSuccessStatusCode)
            {
                _indexReady = false;
                return null;
            }
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            return doc.RootElement.GetProperty("hits").EnumerateArray()
                .Select(x => x.GetProperty("id").GetInt32()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meilisearch query failed; using database search");
            return null;
        }
    }

    private async Task IndexAsync(CatalogWork work)
    {
        using var client = MeiliClient();
        if (client is null || !await EnsureIndexAsync(client)) return;
        try
        {
            using var response = await client.PutAsJsonAsync($"indexes/{IndexName}/documents", new[]
            {
                new { id = work.Id, category = (int)work.Kategori, name = work.Ad, altName = work.AltName }
            });
            if (!response.IsSuccessStatusCode)
            {
                _indexReady = false;
                _logger.LogWarning("Meilisearch indexing returned {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meilisearch indexing failed; database copy remains available");
        }
    }
}
