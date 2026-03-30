using System.ComponentModel.DataAnnotations;

namespace KategoriSecici.Models;

public class AppUser
{
    public int Id { get; set; }

    [Required]
    [StringLength(40)]
    public string UserName { get; set; } = string.Empty;

    [Required]
    [StringLength(180)]
    public string Email { get; set; } = string.Empty;

    [StringLength(260)]
    public string? DisplayName { get; set; }

    [StringLength(1024)]
    public string? PasswordHash { get; set; }

    public bool EmailVerified { get; set; } = false;

    [StringLength(120)]
    public string? EmailVerificationToken { get; set; }

    public DateTime? EmailVerificationExpiresAt { get; set; }

    [StringLength(30)]
    public string AuthProvider { get; set; } = "local";

    [StringLength(260)]
    public string? GoogleSubject { get; set; }

    [StringLength(400)]
    public string? CoverImagePath { get; set; }

    [StringLength(400)]
    public string? AvatarImagePath { get; set; }

    public double AvatarZoom { get; set; } = 1.0;

    public int AvatarPosX { get; set; } = 50;

    public int AvatarPosY { get; set; } = 50;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
