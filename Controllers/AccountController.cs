using System.Security.Claims;
using KategoriSecici.Data;
using KategoriSecici.Models;
using KategoriSecici.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

namespace KategoriSecici.Controllers;

public class AccountController : Controller
{
    private readonly AppDbContext _dbContext;
    private readonly PasswordHasher<AppUser> _passwordHasher;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public AccountController(AppDbContext dbContext, PasswordHasher<AppUser> passwordHasher, IConfiguration configuration, IWebHostEnvironment environment)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _configuration = configuration;
        _environment = environment;
    }

    [HttpGet]
    public IActionResult Login(string lang = "tr", string? returnUrl = null, string? errorMessage = null)
    {
        var currentLang = NormalizeLang(lang);
        ViewData["Lang"] = currentLang;
        ViewData["BodyClass"] = "auth-page";
        ViewData["HideTopNav"] = true;
        return View(new LoginViewModel
        {
            Lang = currentLang,
            ReturnUrl = returnUrl,
            ErrorMessage = errorMessage
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        model.Lang = NormalizeLang(model.Lang);
        ViewData["Lang"] = model.Lang;
        ViewData["BodyClass"] = "auth-page";
        ViewData["HideTopNav"] = true;

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var email = model.Email.Trim().ToLowerInvariant();
        var user = await _dbContext.AppUsers.FirstOrDefaultAsync(x => x.Email == email);

        if (user is null)
        {
            model.ErrorMessage = model.Lang == "en" ? "Account not found. Please sign up first." : "Hesap bulunamadi. Once kaydolmalisin.";
            return View(model);
        }

        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            model.ErrorMessage = model.Lang == "en"
                ? "This account uses Google sign-in. Please continue with Google."
                : "Bu hesap Google ile acilmis. Lutfen Google ile giris yap.";
            return View(model);
        }

        var verify = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, model.Password);
        if (verify == PasswordVerificationResult.Failed)
        {
            model.ErrorMessage = model.Lang == "en" ? "Invalid email or password." : "E-posta veya parola hatali.";
            return View(model);
        }

        await SignInAsync(user, model.RememberMe);
        return RedirectToSafeReturn(model.ReturnUrl, model.Lang);
    }

    [HttpGet]
    public IActionResult Register(string lang = "tr", string? returnUrl = null)
    {
        var currentLang = NormalizeLang(lang);
        ViewData["Lang"] = currentLang;
        ViewData["BodyClass"] = "auth-page";
        ViewData["HideTopNav"] = true;
        return View(new RegisterViewModel
        {
            Lang = currentLang,
            ReturnUrl = returnUrl
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        model.Lang = NormalizeLang(model.Lang);
        ViewData["Lang"] = model.Lang;
        ViewData["BodyClass"] = "auth-page";
        ViewData["HideTopNav"] = true;

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var email = model.Email.Trim().ToLowerInvariant();
        var existing = await _dbContext.AppUsers.FirstOrDefaultAsync(x => x.Email == email);
        if (existing is not null)
        {
            model.ErrorMessage = model.Lang == "en" ? "This e-mail is already registered." : "Bu e-posta zaten kayitli.";
            return View(model);
        }

        var requestedUserName = NormalizeUserName(model.UserName);
        if (string.IsNullOrWhiteSpace(requestedUserName))
        {
            model.ErrorMessage = model.Lang == "en" ? "Username is invalid." : "Kullanici adi gecersiz.";
            return View(model);
        }

        var userNameTaken = await _dbContext.AppUsers.AnyAsync(x => x.UserName.ToLower() == requestedUserName.ToLower());
        if (userNameTaken)
        {
            model.ErrorMessage = model.Lang == "en" ? "Username is already taken." : "Bu kullanici adi dolu.";
            return View(model);
        }

        var user = new AppUser
        {
            UserName = requestedUserName,
            Email = email,
            DisplayName = requestedUserName,
            AuthProvider = "local"
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, model.Password);

        _dbContext.AppUsers.Add(user);
        await _dbContext.SaveChangesAsync();

        await SignInAsync(user, true);
        return RedirectToSafeReturn(model.ReturnUrl, model.Lang);
    }

    [HttpGet]
    public IActionResult GoogleLogin(string lang = "tr", string? returnUrl = null)
    {
        var clientId = _configuration["Authentication:Google:ClientId"] ?? Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
        var clientSecret = _configuration["Authentication:Google:ClientSecret"] ?? Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            var currentLang = NormalizeLang(lang);
            return RedirectToAction(nameof(Login), new
            {
                lang = currentLang,
                returnUrl,
                errorMessage = currentLang == "en" ? "Google login is not configured yet." : "Google girisi henuz ayarlanamadi."
            });
        }

        var redirectUrl = Url.Action(nameof(GoogleResponse), "Account", new { lang = NormalizeLang(lang), returnUrl });
        var properties = new AuthenticationProperties { RedirectUri = redirectUrl };
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    [HttpGet]
    public async Task<IActionResult> GoogleResponse(string lang = "tr", string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction(nameof(Login), new { lang = NormalizeLang(lang), returnUrl });
        }

        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant();
        var displayName = User.FindFirstValue(ClaimTypes.Name)?.Trim();
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(email))
        {
            return RedirectToAction(nameof(Login), new { lang = NormalizeLang(lang), returnUrl });
        }

        var user = await _dbContext.AppUsers.FirstOrDefaultAsync(x => x.Email == email);
        if (user is null)
        {
            user = new AppUser
            {
                UserName = await GenerateUniqueUserNameAsync(email.Split('@')[0]),
                Email = email,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName,
                AuthProvider = "google",
                GoogleSubject = sub
            };
            _dbContext.AppUsers.Add(user);
        }
        else
        {
            user.AuthProvider = "google";
            user.GoogleSubject = sub;
            if (string.IsNullOrWhiteSpace(user.UserName))
            {
                user.UserName = await GenerateUniqueUserNameAsync(email.Split('@')[0]);
            }
            if (string.IsNullOrWhiteSpace(user.DisplayName) && !string.IsNullOrWhiteSpace(displayName))
            {
                user.DisplayName = displayName;
            }
        }

        await _dbContext.SaveChangesAsync();
        await SignInAsync(user, true);
        return RedirectToSafeReturn(returnUrl, NormalizeLang(lang));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(string lang = "tr")
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login), new { lang = NormalizeLang(lang) });
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAccount(DeleteAccountViewModel model)
    {
        var lang = NormalizeLang(model.Lang);
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return RedirectToAction(nameof(Login), new { lang });
        }

        var user = await _dbContext.AppUsers.FirstOrDefaultAsync(x => x.Id == userId.Value);
        if (user is null)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login), new { lang });
        }

        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            TempData["AccountDeleteError"] = lang == "en"
                ? "Google account deletion from this button is disabled. Contact support."
                : "Google hesabi icin bu butondan silme kapali.";
            return RedirectBackOrProfile(lang);
        }

        var verify = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, model.Password ?? string.Empty);
        if (verify == PasswordVerificationResult.Failed)
        {
            TempData["AccountDeleteError"] = lang == "en" ? "Wrong password." : "Parola yanlis.";
            return RedirectBackOrProfile(lang);
        }

        var userItems = await _dbContext.MedyaOgeleri.Where(x => x.AppUserId == user.Id).ToListAsync();
        _dbContext.MedyaOgeleri.RemoveRange(userItems);
        _dbContext.AppUsers.Remove(user);
        await _dbContext.SaveChangesAsync();

        var userDir = Path.Combine(_environment.WebRootPath, "user-media", user.Id.ToString());
        if (Directory.Exists(userDir))
        {
            Directory.Delete(userDir, true);
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Register), new { lang });
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Profile(string lang = "tr")
    {
        var currentLang = NormalizeLang(lang);
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return RedirectToAction(nameof(Login), new { lang = currentLang });
        }

        var user = await _dbContext.AppUsers.FirstOrDefaultAsync(x => x.Id == userId.Value);
        if (user is null)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login), new { lang = currentLang });
        }

        var watchedCounts = await _dbContext.MedyaOgeleri
            .Where(x => x.AppUserId == user.Id && x.Izlendi)
            .GroupBy(x => x.Kategori)
            .Select(g => new { Kategori = g.Key, Count = g.Count() })
            .ToListAsync();

        int CountFor(MedyaKategori k) => watchedCounts.FirstOrDefault(x => x.Kategori == k)?.Count ?? 0;

        ViewData["Lang"] = currentLang;
        ViewData["BodyClass"] = "profile-page";

        var vm = new ProfileViewModel
        {
            Lang = currentLang,
            DisplayName = string.IsNullOrWhiteSpace(user.UserName) ? user.Email : user.UserName,
            CoverImagePath = user.CoverImagePath,
            AvatarImagePath = user.AvatarImagePath,
            AnimeCount = CountFor(MedyaKategori.Anime),
            MangaCount = CountFor(MedyaKategori.Manga),
            KitapCount = CountFor(MedyaKategori.Kitap),
            DiziCount = CountFor(MedyaKategori.Dizi),
            FilmCount = CountFor(MedyaKategori.Film),
            OyunCount = CountFor(MedyaKategori.Oyun)
        };

        return View(vm);
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadProfileMedia(IFormFile? coverFile, IFormFile? avatarFile, string lang = "tr")
    {
        var currentLang = NormalizeLang(lang);
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return RedirectToAction(nameof(Login), new { lang = currentLang });
        }

        var user = await _dbContext.AppUsers.FirstOrDefaultAsync(x => x.Id == userId.Value);
        if (user is null)
        {
            return RedirectToAction(nameof(Login), new { lang = currentLang });
        }

        var userDir = Path.Combine(_environment.WebRootPath, "user-media", user.Id.ToString());
        Directory.CreateDirectory(userDir);

        if (coverFile is not null && coverFile.Length > 0)
        {
            var coverPath = await SaveImageAsync(coverFile, userDir, "cover");
            if (coverPath is not null)
            {
                user.CoverImagePath = $"/user-media/{user.Id}/{coverPath}";
            }
        }

        if (avatarFile is not null && avatarFile.Length > 0)
        {
            var avatarPath = await SaveImageAsync(avatarFile, userDir, "avatar");
            if (avatarPath is not null)
            {
                user.AvatarImagePath = $"/user-media/{user.Id}/{avatarPath}";
            }
        }

        await _dbContext.SaveChangesAsync();
        return RedirectToAction(nameof(Profile), new { lang = currentLang });
    }

    private async Task SignInAsync(AppUser user, bool rememberMe)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Email, user.Email)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var props = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(rememberMe ? 30 : 1)
        };

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);
    }

    private IActionResult RedirectToSafeReturn(string? returnUrl, string lang)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "Home", new { lang, kategori = MedyaKategori.Film });
    }

    private static string NormalizeLang(string? lang)
    {
        return string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";
    }

    private int? GetCurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(raw, out var id) ? id : null;
    }

    private IActionResult RedirectBackOrProfile(string lang)
    {
        if (Request.Headers.TryGetValue("Referer", out StringValues referer) &&
            Uri.TryCreate(referer.ToString(), UriKind.Absolute, out var refererUri) &&
            string.Equals(refererUri.Host, Request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return Redirect(refererUri.PathAndQuery);
        }

        return RedirectToAction(nameof(Profile), new { lang });
    }

    private static async Task<string?> SaveImageAsync(IFormFile file, string directory, string baseName)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowed = new HashSet<string> { ".jpg", ".jpeg", ".png", ".webp" };
        if (!allowed.Contains(ext))
        {
            return null;
        }

        if (file.Length > 8 * 1024 * 1024)
        {
            return null;
        }

        var fileName = $"{baseName}{ext}";
        var fullPath = Path.Combine(directory, fileName);
        await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await file.CopyToAsync(stream);
        return fileName;
    }

    private static string NormalizeUserName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var filtered = new string(value.Trim().Where(ch =>
            char.IsLetterOrDigit(ch) || ch == '_' || ch == '.').ToArray());
        return filtered.Length > 40 ? filtered[..40] : filtered;
    }

    private async Task<string> GenerateUniqueUserNameAsync(string seed)
    {
        var baseName = NormalizeUserName(seed);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "kullanici";
        }

        var candidate = baseName;
        var i = 1;
        while (await _dbContext.AppUsers.AnyAsync(x => x.UserName.ToLower() == candidate.ToLower()))
        {
            candidate = $"{baseName}{i}";
            if (candidate.Length > 40)
            {
                candidate = candidate[..40];
            }
            i++;
        }

        return candidate;
    }
}
