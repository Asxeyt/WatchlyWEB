using System.ComponentModel.DataAnnotations;

namespace KategoriSecici.ViewModels;

public class RegisterViewModel
{
    [Required]
    [StringLength(60)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Compare(nameof(Password))]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string Lang { get; set; } = "tr";

    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }
}
