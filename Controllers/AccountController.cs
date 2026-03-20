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

namespace KategoriSecici.Controllers;

public class AccountController : Controller
{
    private readonly AppDbContext _dbContext;
    private readonly PasswordHasher<AppUser> _passwordHasher;
    private readonly IConfiguration _configuration;

    public AccountController(AppDbContext dbContext, PasswordHasher<AppUser> passwordHasher, IConfiguration configuration)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _configuration = configuration;
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

        if (user is null || string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            model.ErrorMessage = model.Lang == "en" ? "Invalid email or password." : "E-posta veya parola hatali.";
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

        var user = new AppUser
        {
            Email = email,
            DisplayName = model.DisplayName.Trim(),
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
    [HttpGet]
    public IActionResult Profile(string lang = "tr")
    {
        ViewData["Lang"] = NormalizeLang(lang);
        ViewData["BodyClass"] = "settings-page";
        return View();
    }

    private async Task SignInAsync(AppUser user, bool rememberMe)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName ?? user.Email),
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
}
