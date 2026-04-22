namespace KategoriSecici.ViewModels;

public class ProfileViewModel
{
    public string Lang { get; set; } = "tr";

    public string DisplayName { get; set; } = "Profil";

    public string? CoverImagePath { get; set; }

    public string? AvatarImagePath { get; set; }

    public double AvatarZoom { get; set; } = 1.0;

    public int AvatarPosX { get; set; } = 50;

    public int AvatarPosY { get; set; } = 50;

    public int AnimeCount { get; set; }
    public int MangaCount { get; set; }
    public int KitapCount { get; set; }
    public int DiziCount { get; set; }
    public int FilmCount { get; set; }
    public int OyunCount { get; set; }
    public int CizgiFilmCount { get; set; }
    public int CizgiRomanCount { get; set; }
    public int WebtoonCount { get; set; }

    public string AnimeMuk { get; set; } = "-";
    public string? AnimeMukPoster { get; set; }
    public string MangaMuk { get; set; } = "-";
    public string? MangaMukPoster { get; set; }
    public string KitapMuk { get; set; } = "-";
    public string? KitapMukPoster { get; set; }
    public string DiziMuk { get; set; } = "-";
    public string? DiziMukPoster { get; set; }
    public string FilmMuk { get; set; } = "-";
    public string? FilmMukPoster { get; set; }
    public string OyunMuk { get; set; } = "-";
    public string? OyunMukPoster { get; set; }
    public string CizgiFilmMuk { get; set; } = "-";
    public string? CizgiFilmMukPoster { get; set; }
    public string CizgiRomanMuk { get; set; } = "-";
    public string? CizgiRomanMukPoster { get; set; }
    public string WebtoonMuk { get; set; } = "-";
    public string? WebtoonMukPoster { get; set; }
}
