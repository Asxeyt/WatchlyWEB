using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Concurrent;
using KategoriSecici.Models;
using KategoriSecici.ViewModels;

namespace KategoriSecici.Data;

// Keyless sources work immediately; keyed sources activate only when configured.
public sealed class CatalogSources
{
    private static readonly SemaphoreSlim IgdbTokenLock = new(1, 1);
    private static string? _igdbToken;
    private static DateTime _igdbTokenExpires;
    private static readonly ConcurrentDictionary<string, (DateTime Expires, List<CatalogSuggestionViewModel> Items)> WikidataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _config;
    private readonly ILogger<CatalogSources> _logger;

    public CatalogSources(IHttpClientFactory clients, IConfiguration config, ILogger<CatalogSources> logger)
    {
        _clients = clients;
        _config = config;
        _logger = logger;
    }

    public async Task<List<CatalogSuggestionViewModel>> SearchAsync(MedyaKategori category, string query, string lang)
    {
        var tasks = category switch
        {
            MedyaKategori.Film => new[] { SearchWikidataFilmsAsync(query, lang), SearchTmdbAsync(false, query, lang), SearchTraktAsync(false, query) },
            MedyaKategori.Dizi => new[] { SearchTmdbAsync(true, query, lang), SearchTraktAsync(true, query) },
            MedyaKategori.Anime => new[] { SearchKitsuAsync(true, query) },
            MedyaKategori.Manga => new[] { SearchKitsuAsync(false, query) },
            MedyaKategori.Oyun => new[] { SearchRawgAsync(query), SearchIgdbAsync(query), SearchGiantBombAsync(query) },
            _ => Array.Empty<Task<List<CatalogSuggestionViewModel>>>()
        };
        if (tasks.Length == 0) return new();
        await Task.WhenAll(tasks);
        return tasks.SelectMany(x => x.Result).ToList();
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchWikidataFilmsAsync(string query, string lang)
    {
        if (query.Trim().Length < 3) return new();
        var cacheKey = $"{lang}|{query.Trim()}";
        if (WikidataCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow)
            return cached.Items.Select(Clone).ToList();

        var searchUrl = $"https://www.wikidata.org/w/api.php?action=wbsearchentities&search={Uri.EscapeDataString(query)}&language=en&type=item&limit=20&format=json";
        using var searchRequest = WikidataRequest(searchUrl);
        using var searchDoc = await FetchAsync(searchRequest);
        if (searchDoc is null || !searchDoc.RootElement.TryGetProperty("search", out var searchRows) || searchRows.ValueKind != JsonValueKind.Array)
            return new();
        var ids = searchRows.EnumerateArray().Select(x => Str(x, "id"))
            .Where(x => x.StartsWith('Q') && x[1..].All(char.IsDigit)).Distinct().ToList();
        if (ids.Count == 0) return new();

        var idsParam = Uri.EscapeDataString(string.Join('|', ids));
        var entitiesUrl = $"https://www.wikidata.org/w/api.php?action=wbgetentities&ids={idsParam}&props=labels%7Cdescriptions%7Cclaims&languages=en%7Ctr&format=json";
        using var entitiesRequest = WikidataRequest(entitiesUrl);
        using var entitiesDoc = await FetchAsync(entitiesRequest);
        if (entitiesDoc is null || !entitiesDoc.RootElement.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
            return new();

        var results = new List<CatalogSuggestionViewModel>();
        foreach (var id in ids)
        {
            if (!entities.TryGetProperty(id, out var entity) || !entity.TryGetProperty("claims", out var claims)) continue;
            var types = ClaimIds(claims, "P31");
            if (!types.Overlaps(new[] { "Q11424", "Q24869", "Q24862", "Q506240", "Q202866" })) continue;
            if (types.Contains("Q20650540") || ClaimIds(claims, "P136").Contains("Q1107")) continue;
            var englishName = Label(entity, "labels", "en");
            var turkishName = Label(entity, "labels", "tr");
            var name = englishName.Length > 0 ? englishName : turkishName;
            if (name.Length == 0) continue;
            var description = Label(entity, "descriptions", lang == "en" ? "en" : "tr");
            if (description.Length == 0) description = Label(entity, "descriptions", "en");
            var item = new CatalogSuggestionViewModel
            {
                Name = name,
                AltName = turkishName != name ? turkishName : string.Empty,
                Summary = description,
                ReleaseDate = ClaimDate(claims, "P577"),
                Creator = "Wikidata"
            };
            AddId(item, "wikidata", id);
            var imdb = ClaimString(claims, "P345");
            if (imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) AddId(item, "imdb", imdb);
            results.Add(item);
        }

        if (WikidataCache.Count >= 256)
        {
            foreach (var entry in WikidataCache.Where(x => x.Value.Expires <= DateTime.UtcNow))
                WikidataCache.TryRemove(entry.Key, out _);
            if (WikidataCache.Count >= 512) WikidataCache.Clear();
        }
        WikidataCache[cacheKey] = (DateTime.UtcNow.AddMinutes(10), results.Select(Clone).ToList());
        return results;
    }

    private static HttpRequestMessage WikidataRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("WatchlyWEB/2.0 (https://github.com/Asxeyt/WatchlyWEB)");
        return request;
    }

    private static HashSet<string> ClaimIds(JsonElement claims, string property)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!claims.TryGetProperty(property, out var rows) || rows.ValueKind != JsonValueKind.Array) return values;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var data) &&
                data.TryGetProperty("value", out var value))
            {
                var id = Str(value, "id");
                if (id.Length > 0) values.Add(id);
            }
        }
        return values;
    }

    private static string ClaimString(JsonElement claims, string property)
    {
        if (!claims.TryGetProperty(property, out var rows) || rows.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var data) &&
                data.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string ClaimDate(JsonElement claims, string property)
    {
        if (!claims.TryGetProperty(property, out var rows) || rows.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var data) &&
                data.TryGetProperty("value", out var value))
            {
                var time = Str(value, "time").TrimStart('+');
                if (time.Length >= 10) return time[..10];
            }
        }
        return string.Empty;
    }

    private static string Label(JsonElement entity, string property, string language)
    {
        if (!entity.TryGetProperty(property, out var values) || !values.TryGetProperty(language, out var row)) return string.Empty;
        return Str(row, "value");
    }

    private static CatalogSuggestionViewModel Clone(CatalogSuggestionViewModel source) => new()
    {
        Name = source.Name, AltName = source.AltName, Summary = source.Summary, ReleaseDate = source.ReleaseDate,
        Creator = source.Creator, ExternalIds = new Dictionary<string, string>(source.ExternalIds, StringComparer.OrdinalIgnoreCase)
    };

    private async Task<List<CatalogSuggestionViewModel>> SearchTmdbAsync(bool tv, string query, string lang)
    {
        var token = _config["TMDB_READ_ACCESS_TOKEN"];
        var key = _config["TMDB_API_KEY"];
        if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(key)) return new();

        var type = tv ? "tv" : "movie";
        var language = lang == "en" ? "en-US" : "tr-TR";
        var url = $"https://api.themoviedb.org/3/search/{type}?query={Uri.EscapeDataString(query)}&language={language}&include_adult=false";
        if (string.IsNullOrWhiteSpace(token)) url += $"&api_key={Uri.EscapeDataString(key!)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new("Bearer", token);
        using var doc = await FetchAsync(request);
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array) return new();

        var results = new List<CatalogSuggestionViewModel>();
        foreach (var row in rows.EnumerateArray().Take(12))
        {
            if (row.TryGetProperty("adult", out var adult) && adult.ValueKind == JsonValueKind.True) continue;
            var animated = row.TryGetProperty("genre_ids", out var genres) && genres.ValueKind == JsonValueKind.Array &&
                genres.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Number && x.GetInt32() == 16);
            // Japanese animation belongs to Anime, not Film or Dizi.
            if (animated && Str(row, "original_language") == "ja") continue;
            var name = Str(row, tv ? "name" : "title");
            var id = Str(row, "id");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
            var poster = Str(row, "poster_path");
            var score = Num(row, "vote_average");
            var item = new CatalogSuggestionViewModel
            {
                Name = name,
                AltName = Str(row, tv ? "original_name" : "original_title"),
                PosterUrl = string.IsNullOrWhiteSpace(poster) ? string.Empty : $"https://image.tmdb.org/t/p/w342{poster}",
                Summary = Str(row, "overview"),
                Score = score > 0 ? score.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty,
                ReleaseDate = Str(row, tv ? "first_air_date" : "release_date"),
                Creator = "TMDB"
            };
            item.ExternalIds[tv ? "tmdb_tv" : "tmdb_movie"] = id;
            results.Add(item);
        }

        // Search results omit IMDb IDs. Resolve them for cross-provider deduplication.
        await Task.WhenAll(results.Take(5).Select(async item =>
        {
            var tmdbId = item.ExternalIds[tv ? "tmdb_tv" : "tmdb_movie"];
            var externalUrl = $"https://api.themoviedb.org/3/{type}/{tmdbId}/external_ids";
            if (string.IsNullOrWhiteSpace(token)) externalUrl += $"?api_key={Uri.EscapeDataString(key!)}";
            using var externalRequest = new HttpRequestMessage(HttpMethod.Get, externalUrl);
            if (!string.IsNullOrWhiteSpace(token)) externalRequest.Headers.Authorization = new("Bearer", token);
            using var externalDoc = await FetchAsync(externalRequest);
            if (externalDoc is null) return;
            var imdb = Str(externalDoc.RootElement, "imdb_id");
            if (!string.IsNullOrWhiteSpace(imdb)) item.ExternalIds["imdb"] = imdb;
        }));
        return results;
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchTraktAsync(bool tv, string query)
    {
        var key = _config["TRAKT_CLIENT_ID"];
        if (string.IsNullOrWhiteSpace(key)) return new();
        var type = tv ? "show" : "movie";
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.trakt.tv/search/{type}?query={Uri.EscapeDataString(query)}&limit=10");
        request.Headers.TryAddWithoutValidation("trakt-api-key", key);
        request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
        using var doc = await FetchAsync(request);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return new();
        var results = new List<CatalogSuggestionViewModel>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty(type, out var work)) continue;
            var name = Str(work, "title");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var item = new CatalogSuggestionViewModel { Name = name, ReleaseDate = Str(work, "year"), Creator = "Trakt" };
            if (work.TryGetProperty("ids", out var ids))
            {
                AddId(item, "imdb", Str(ids, "imdb"));
                AddId(item, tv ? "tmdb_tv" : "tmdb_movie", Str(ids, "tmdb"));
                AddId(item, "trakt", Str(ids, "trakt"));
            }
            results.Add(item);
        }
        return results;
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchKitsuAsync(bool anime, string query)
    {
        var type = anime ? "anime" : "manga";
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://kitsu.io/api/edge/{type}?filter%5Btext%5D={Uri.EscapeDataString(query)}&page%5Blimit%5D=10");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.api+json");
        using var doc = await FetchAsync(request);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array) return new();
        var results = new List<CatalogSuggestionViewModel>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("attributes", out var attr)) continue;
            var name = Str(attr, "canonicalTitle");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var poster = string.Empty;
            if (attr.TryGetProperty("posterImage", out var image) && image.ValueKind == JsonValueKind.Object)
                poster = Str(image, "small");
            var ratingText = Str(attr, "averageRating");
            var rating = double.TryParse(ratingText, NumberStyles.Any, CultureInfo.InvariantCulture, out var percent)
                ? (percent / 10).ToString("0.0", CultureInfo.InvariantCulture) : string.Empty;
            var item = new CatalogSuggestionViewModel
            {
                Name = name,
                PosterUrl = poster,
                Summary = Str(attr, "synopsis"),
                Score = rating,
                ReleaseDate = Str(attr, "startDate"),
                Creator = "Kitsu"
            };
            AddId(item, "kitsu", Str(row, "id"));
            results.Add(item);
        }
        return results;
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchRawgAsync(string query)
    {
        var key = _config["RAWG_API_KEY"];
        if (string.IsNullOrWhiteSpace(key)) return new();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.rawg.io/api/games?key={Uri.EscapeDataString(key)}&search={Uri.EscapeDataString(query)}&page_size=10");
        using var doc = await FetchAsync(request);
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array) return new();
        var results = new List<CatalogSuggestionViewModel>();
        foreach (var row in rows.EnumerateArray())
        {
            var name = Str(row, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var genre = row.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array
                ? string.Join(", ", genres.EnumerateArray().Take(3).Select(x => Str(x, "name"))) : string.Empty;
            var rating = Num(row, "rating");
            var item = new CatalogSuggestionViewModel
            {
                Name = name,
                Genre = genre,
                PosterUrl = Str(row, "background_image"),
                Score = rating > 0 ? (rating * 2).ToString("0.0", CultureInfo.InvariantCulture) : string.Empty,
                ReleaseDate = Str(row, "released"),
                Creator = "RAWG"
            };
            AddId(item, "rawg", Str(row, "id"));
            results.Add(item);
        }
        return results;
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchIgdbAsync(string query)
    {
        var clientId = _config["IGDB_CLIENT_ID"];
        var clientSecret = _config["IGDB_CLIENT_SECRET"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)) return new();
        var token = await GetIgdbTokenAsync(clientId, clientSecret);
        if (string.IsNullOrWhiteSpace(token)) return new();
        var safeQuery = query.Replace("\\", string.Empty).Replace("\"", string.Empty).Replace(";", string.Empty);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games");
        request.Headers.TryAddWithoutValidation("Client-ID", clientId);
        request.Headers.Authorization = new("Bearer", token);
        request.Content = new StringContent($"search \"{safeQuery}\"; fields id,name,summary,rating,first_release_date,cover.url; limit 10;");
        using var doc = await FetchAsync(request);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return new();
        var results = new List<CatalogSuggestionViewModel>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var name = Str(row, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var poster = row.TryGetProperty("cover", out var cover) ? Str(cover, "url") : string.Empty;
            if (poster.StartsWith("//", StringComparison.Ordinal)) poster = "https:" + poster;
            poster = poster.Replace("t_thumb", "t_cover_big", StringComparison.OrdinalIgnoreCase);
            var release = Num(row, "first_release_date");
            var rating = Num(row, "rating");
            var item = new CatalogSuggestionViewModel
            {
                Name = name,
                PosterUrl = poster,
                Summary = Str(row, "summary"),
                Score = rating > 0 ? (rating / 10).ToString("0.0", CultureInfo.InvariantCulture) : string.Empty,
                ReleaseDate = release > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)release).ToString("yyyy-MM-dd") : string.Empty,
                Creator = "IGDB"
            };
            AddId(item, "igdb", Str(row, "id"));
            results.Add(item);
        }
        return results;
    }

    private async Task<string?> GetIgdbTokenAsync(string clientId, string secret)
    {
        if (_igdbTokenExpires > DateTime.UtcNow.AddMinutes(5)) return _igdbToken;
        await IgdbTokenLock.WaitAsync();
        try
        {
            if (_igdbTokenExpires > DateTime.UtcNow.AddMinutes(5)) return _igdbToken;
            using var client = _clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);
            using var response = await client.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["client_secret"] = secret, ["grant_type"] = "client_credentials"
            }));
            if (!response.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            _igdbToken = Str(doc.RootElement, "access_token");
            var seconds = Num(doc.RootElement, "expires_in");
            _igdbTokenExpires = DateTime.UtcNow.AddSeconds(seconds > 0 ? seconds : 3600);
            return _igdbToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IGDB authentication failed");
            return null;
        }
        finally { IgdbTokenLock.Release(); }
    }

    private async Task<List<CatalogSuggestionViewModel>> SearchGiantBombAsync(string query)
    {
        var key = _config["GIANTBOMB_API_KEY"];
        if (string.IsNullOrWhiteSpace(key)) return new();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.giantbomb.com/api/search/?api_key={Uri.EscapeDataString(key)}&format=json&resources=game&query={Uri.EscapeDataString(query)}&limit=10");
        request.Headers.TryAddWithoutValidation("User-Agent", "Watchly/2.0");
        using var doc = await FetchAsync(request);
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array) return new();
        var results = new List<CatalogSuggestionViewModel>();
        foreach (var row in rows.EnumerateArray())
        {
            var name = Str(row, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var poster = row.TryGetProperty("image", out var image) ? Str(image, "small_url") : string.Empty;
            var item = new CatalogSuggestionViewModel
            {
                Name = name,
                PosterUrl = poster,
                Summary = Str(row, "deck"),
                ReleaseDate = Str(row, "original_release_date"),
                Creator = "Giant Bomb"
            };
            AddId(item, "giantbomb", Str(row, "guid"));
            results.Add(item);
        }
        return results;
    }

    private async Task<JsonDocument?> FetchAsync(HttpRequestMessage request)
    {
        try
        {
            using var client = _clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Catalog provider request failed: {Host}", request.RequestUri?.Host);
            return null;
        }
    }

    private static void AddId(CatalogSuggestionViewModel item, string source, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) item.ExternalIds[source] = value;
    }

    private static string Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static double Num(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
    }
}
