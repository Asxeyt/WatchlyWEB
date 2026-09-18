# Watchly v2.0

Watchly is Asxeyt's ASP.NET Core media tracker. The visible categories are Film, Dizi, Anime, Manga, Kitap and Oyun. Older Cizgi Film, Cizgi Roman and Webtoon rows are preserved in user data but are no longer shown or searchable.

## Catalog design

- PostgreSQL (recommended on Render) or SQLite stores a shared `CatalogWorks` table separately from user lists. Selected list entries retain a nullable `CatalogWorkId` link. `CatalogExternalIds` has a unique `(category, source, external ID)` index.
- Movie/series IDs use TMDB and IMDb IDs when available. Anime/manga use MyAnimeList IDs from Jikan; books use normalized ISBN-13 plus Open Library/Google IDs. IDs, not title alone, are the primary merge key. Exact title and release year can merge non-book records when cross-provider IDs are missing.
- Search suggestions are filtered by the active category. Selecting a suggestion fills the add form and, when cached, submits its catalog ID. First searches query the external providers; returned works are stored for later searches.
- Meilisearch is the optional search index. The primary data remains in PostgreSQL/SQLite. Without Meilisearch, database search still works but is not suitable for a very large catalog.
- This is **not** a preloaded database of every work in the world. Source coverage, API terms and quotas make that impossible to guarantee. Do not bulk-harvest Open Library or AniList.

## Sources and configuration

Immediately usable without credentials: Wikidata (typed film fallback), Jikan, Kitsu, Google Books, Open Library, TVmaze, Steam and iTunes. Providers can fail or rate-limit; previously cached results remain usable. Wikidata labels/descriptions are CC0 data; film results are checked against its structured item types, rather than accepting every text match.

Optional Render environment variables:

| Purpose | Variables |
| --- | --- |
| PostgreSQL | `DATABASE_URL` (or `NEON_DATABASE_URL`) |
| Meilisearch | `MEILI_URL`, `MEILI_API_KEY` (server-side admin key; never expose in the browser) |
| Movies and TV | `TMDB_READ_ACCESS_TOKEN` or `TMDB_API_KEY`, `TRAKT_CLIENT_ID`, `OMDB_API_KEY` |
| Games | `RAWG_API_KEY`, `IGDB_CLIENT_ID` + `IGDB_CLIENT_SECRET`, `GIANTBOMB_API_KEY` |
| Login/email | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASS`, `SMTP_FROM` |

For production, deploy a separate persistent Meilisearch service, set its master key, then point the web service at it with `MEILI_URL` and `MEILI_API_KEY`. Meilisearch is not included in the web container. The app falls back to database search if the index is unavailable. It creates the catalog tables on startup for existing v1.x databases; existing user lists are not deleted. A Render web service without PostgreSQL or a persistent disk loses its SQLite catalog and user data on a rebuild/redeploy; configure persistent storage before relying on the catalog.

AniList's current terms prohibit use by competing anime/manga list or tracker services, so Watchly does not enable it. IMDb via AWS Data Exchange requires a subscription and ingestion contract; Goodreads/ISBNDB are not enabled without access. Obtain permission and credentials before adding them. TMDB requires attribution (shown in Settings) and may require a commercial license depending on usage.

## Local run and deployment

Requires .NET 8 SDK. Run `dotnet restore` and `dotnet run`. Without `DATABASE_URL`, the app uses local SQLite. Set secrets through environment variables, not committed files.

For Render: push the updated Git repository to GitHub, set the optional environment variables, and use **Manual Deploy → Deploy latest commit**. Uploading a ZIP to Render or editing a local folder alone will not update the live site.

The `WatchlyRelease` Asxeyt signature check is intentionally retained. Removing or changing the author/signature invalidates startup.
