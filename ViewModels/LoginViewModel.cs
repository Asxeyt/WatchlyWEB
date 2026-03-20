using System.ComponentModel.DataAnnotations;

namespace KategoriSecici.ViewModels;

public class LoginViewModel
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; } = true;

    public string Lang { get; set; } = "tr";

    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }
}
