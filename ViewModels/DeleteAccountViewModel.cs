using System.ComponentModel.DataAnnotations;

namespace KategoriSecici.ViewModels;

public class DeleteAccountViewModel
{
    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public string Lang { get; set; } = "tr";
}
