namespace KategoriSecici.Models;

public class AppUserMedia
{
    public int AppUserId { get; set; }

    public byte[]? AvatarBytes { get; set; }
    public string? AvatarContentType { get; set; }

    public byte[]? CoverBytes { get; set; }
    public string? CoverContentType { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

