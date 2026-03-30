using System.IO;
using KategoriSecici.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(renderPort))
{
    builder.WebHost.UseUrls($"http://*:{renderPort}");
}

var configuredConnection = builder.Configuration.GetConnectionString("DefaultConnection");
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? Environment.GetEnvironmentVariable("NEON_DATABASE_URL");
var explicitDbPath = Environment.GetEnvironmentVariable("DATABASE_PATH");
var renderDiskPath = Environment.GetEnvironmentVariable("RENDER_DISK_PATH");
if (string.IsNullOrWhiteSpace(renderDiskPath) && !string.IsNullOrWhiteSpace(renderPort))
{
    renderDiskPath = "/var/data";
}

var usePostgres = !string.IsNullOrWhiteSpace(databaseUrl) ||
                  (!string.IsNullOrWhiteSpace(configuredConnection) &&
                   (configuredConnection.Contains("Host=", StringComparison.OrdinalIgnoreCase) ||
                    configuredConnection.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)));

string connectionString;
if (usePostgres)
{
    connectionString = NormalizePostgresConnectionString(databaseUrl ?? configuredConnection ?? string.Empty);
}
else if (!string.IsNullOrWhiteSpace(explicitDbPath))
{
    var dir = Path.GetDirectoryName(explicitDbPath);
    if (!string.IsNullOrWhiteSpace(dir))
    {
        Directory.CreateDirectory(dir);
    }
    connectionString = $"Data Source={explicitDbPath}";
}
else if (!string.IsNullOrWhiteSpace(renderDiskPath))
{
    Directory.CreateDirectory(renderDiskPath);
    var diskDbPath = Path.Combine(renderDiskPath, "kategorisecici.db");
    if (!File.Exists(diskDbPath))
    {
        var legacyDbPath = Path.Combine(builder.Environment.ContentRootPath, "kategorisecici.db");
        if (File.Exists(legacyDbPath))
        {
            File.Copy(legacyDbPath, diskDbPath, overwrite: false);
        }
    }
    connectionString = $"Data Source={diskDbPath}";
}
else if (!string.IsNullOrWhiteSpace(configuredConnection))
{
    connectionString = configuredConnection;
}
else
{
    connectionString = $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "kategorisecici.db")}";
}

var keyStorePath = Path.Combine(builder.Environment.ContentRootPath, ".keys");
if (!string.IsNullOrWhiteSpace(renderDiskPath))
{
    keyStorePath = Path.Combine(renderDiskPath, "keys");
}
Directory.CreateDirectory(keyStorePath);

builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (usePostgres)
    {
        options.UseNpgsql(connectionString);
    }
    else
    {
        options.UseSqlite(connectionString);
    }
});
builder.Services.AddScoped<PasswordHasher<KategoriSecici.Models.AppUser>>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });

var googleClientId = builder.Configuration["Authentication:Google:ClientId"] ?? Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET");
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    builder.Services.AddAuthentication()
        .AddGoogle(options =>
        {
            options.ClientId = googleClientId;
            options.ClientSecret = googleClientSecret;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        });
}

builder.Services.AddAuthorization();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyStorePath))
    .SetApplicationName("KategoriSecici");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    if (db.Database.IsSqlite())
    {
        ApplySqliteLegacyFixes(db);
    }
    else if (db.Database.IsNpgsql())
    {
        ApplyPostgresLegacyFixes(db);
    }

    SeedData.Initialize(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Anasayfa}/{id?}");

app.Run();

static void ApplySqliteLegacyFixes(AppDbContext db)
{
    var dbConnection = db.Database.GetDbConnection();
    dbConnection.Open();

    using var cmd = dbConnection.CreateCommand();
    cmd.CommandText = "PRAGMA table_info('MedyaOgeleri');";
    var hasIzlendi = false;
    var hasAppUserId = false;
    var hasPosterUrl = false;
    var hasTur = false;
    var hasKonu = false;
    var hasPuan = false;
    var hasFiyat = false;
    using (var reader = cmd.ExecuteReader())
    {
        while (reader.Read())
        {
            if (string.Equals(reader["name"]?.ToString(), "Izlendi", StringComparison.OrdinalIgnoreCase))
            {
                hasIzlendi = true;
            }

            if (string.Equals(reader["name"]?.ToString(), "AppUserId", StringComparison.OrdinalIgnoreCase))
            {
                hasAppUserId = true;
            }
            if (string.Equals(reader["name"]?.ToString(), "PosterUrl", StringComparison.OrdinalIgnoreCase))
            {
                hasPosterUrl = true;
            }
            if (string.Equals(reader["name"]?.ToString(), "Tur", StringComparison.OrdinalIgnoreCase))
            {
                hasTur = true;
            }
            if (string.Equals(reader["name"]?.ToString(), "Konu", StringComparison.OrdinalIgnoreCase))
            {
                hasKonu = true;
            }
            if (string.Equals(reader["name"]?.ToString(), "Puan", StringComparison.OrdinalIgnoreCase))
            {
                hasPuan = true;
            }
            if (string.Equals(reader["name"]?.ToString(), "Fiyat", StringComparison.OrdinalIgnoreCase))
            {
                hasFiyat = true;
            }
        }
    }

    if (!hasIzlendi)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE MedyaOgeleri ADD COLUMN Izlendi INTEGER NOT NULL DEFAULT 0;");
    }

    if (!hasAppUserId)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE MedyaOgeleri ADD COLUMN AppUserId INTEGER NULL;");
    }
    if (!hasPosterUrl)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE MedyaOgeleri ADD COLUMN PosterUrl TEXT NULL;");
    }
    if (!hasTur)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE MedyaOgeleri ADD COLUMN Tur TEXT NULL;");
    }
    if (!hasKonu)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE MedyaOgeleri ADD COLUMN Konu TEXT NULL;");
    }
    if (!hasPuan)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE MedyaOgeleri ADD COLUMN Puan TEXT NULL;");
    }
    if (!hasFiyat)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE MedyaOgeleri ADD COLUMN Fiyat TEXT NULL;");
    }

    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS AppUsers (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserName TEXT NOT NULL DEFAULT '',
            Email TEXT NOT NULL,
            DisplayName TEXT NULL,
            PasswordHash TEXT NULL,
            EmailVerified INTEGER NOT NULL DEFAULT 0,
            EmailVerificationToken TEXT NULL,
            EmailVerificationExpiresAt TEXT NULL,
            AuthProvider TEXT NOT NULL DEFAULT 'local',
            GoogleSubject TEXT NULL,
            CoverImagePath TEXT NULL,
            AvatarImagePath TEXT NULL,
            CreatedAt TEXT NOT NULL
        );
        """);

    db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_AppUsers_Email ON AppUsers (Email);");
    db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_AppUsers_UserName ON AppUsers (UserName);");

    using var userCmd = dbConnection.CreateCommand();
    userCmd.CommandText = "PRAGMA table_info('AppUsers');";
    var hasUserName = false;
    var hasEmailVerified = false;
    var hasEmailVerificationToken = false;
    var hasEmailVerificationExpiresAt = false;
    var hasCoverImagePath = false;
    var hasAvatarImagePath = false;
    using (var userReader = userCmd.ExecuteReader())
    {
        while (userReader.Read())
        {
            var col = userReader["name"]?.ToString();
            if (string.Equals(col, "UserName", StringComparison.OrdinalIgnoreCase))
            {
                hasUserName = true;
            }
            if (string.Equals(col, "EmailVerified", StringComparison.OrdinalIgnoreCase))
            {
                hasEmailVerified = true;
            }
            if (string.Equals(col, "EmailVerificationToken", StringComparison.OrdinalIgnoreCase))
            {
                hasEmailVerificationToken = true;
            }
            if (string.Equals(col, "EmailVerificationExpiresAt", StringComparison.OrdinalIgnoreCase))
            {
                hasEmailVerificationExpiresAt = true;
            }
            if (string.Equals(col, "CoverImagePath", StringComparison.OrdinalIgnoreCase))
            {
                hasCoverImagePath = true;
            }
            if (string.Equals(col, "AvatarImagePath", StringComparison.OrdinalIgnoreCase))
            {
                hasAvatarImagePath = true;
            }
        }
    }

    if (!hasUserName)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE AppUsers ADD COLUMN UserName TEXT NULL;");
    }
    if (!hasEmailVerified)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE AppUsers ADD COLUMN EmailVerified INTEGER NOT NULL DEFAULT 0;");
    }
    if (!hasEmailVerificationToken)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE AppUsers ADD COLUMN EmailVerificationToken TEXT NULL;");
    }
    if (!hasEmailVerificationExpiresAt)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE AppUsers ADD COLUMN EmailVerificationExpiresAt TEXT NULL;");
    }
    if (!hasCoverImagePath)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE AppUsers ADD COLUMN CoverImagePath TEXT NULL;");
    }
    if (!hasAvatarImagePath)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE AppUsers ADD COLUMN AvatarImagePath TEXT NULL;");
    }

    var users = db.AppUsers.OrderBy(x => x.Id).ToList();
    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var u in users)
    {
        var baseName = string.IsNullOrWhiteSpace(u.UserName)
            ? (string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email.Split('@')[0] : u.DisplayName)
            : u.UserName;

        var normalized = new string(baseName.Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '.').ToArray());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "kullanici";
        }

        if (normalized.Length > 40)
        {
            normalized = normalized[..40];
        }

        var unique = normalized;
        var i = 1;
        while (used.Contains(unique))
        {
            unique = normalized;
            var suffix = i.ToString();
            if (unique.Length + suffix.Length > 40)
            {
                unique = unique[..(40 - suffix.Length)];
            }
            unique += suffix;
            i++;
        }

        used.Add(unique);
        u.UserName = unique;
        if (string.IsNullOrWhiteSpace(u.DisplayName))
        {
            u.DisplayName = unique;
        }
        if (u.AuthProvider == "google")
        {
            u.EmailVerified = true;
        }
    }

    db.SaveChanges();
}

static void ApplyPostgresLegacyFixes(AppDbContext db)
{
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE "MedyaOgeleri"
        ADD COLUMN IF NOT EXISTS "PosterUrl" text NULL,
        ADD COLUMN IF NOT EXISTS "Tur" character varying(240) NULL,
        ADD COLUMN IF NOT EXISTS "Konu" character varying(3000) NULL,
        ADD COLUMN IF NOT EXISTS "Puan" character varying(80) NULL,
        ADD COLUMN IF NOT EXISTS "Fiyat" character varying(80) NULL;
        """);
}

static string NormalizePostgresConnectionString(string raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        throw new InvalidOperationException("PostgreSQL connection string is empty.");
    }

    if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var db = uri.AbsolutePath.Trim('/');

        var sslMode = "Require";
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in query)
        {
            var kv = item.Split('=', 2);
            if (kv.Length == 2 && string.Equals(kv[0], "sslmode", StringComparison.OrdinalIgnoreCase))
            {
                sslMode = kv[1];
            }
        }

        var port = uri.Port > 0 ? uri.Port : 5432;
        return $"Host={uri.Host};Port={port};Database={db};Username={user};Password={pass};SSL Mode={sslMode};Trust Server Certificate=true";
    }

    return raw;
}
