namespace KategoriSecici.ViewModels;

public class ProfileViewModel
{
    public string Lang { get; set; } = "tr";

    public string DisplayName { get; set; } = "Profil";

    public string? CoverImagePath { get; set; }

    public string? AvatarImagePath { get; set; }

    public int AnimeCount { get; set; }
    public int MangaCount { get; set; }
    public int KitapCount { get; set; }
    public int DiziCount { get; set; }
    public int FilmCount { get; set; }
    public int OyunCount { get; set; }
}
