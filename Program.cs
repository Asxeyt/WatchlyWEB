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
var explicitDbPath = Environment.GetEnvironmentVariable("DATABASE_PATH");
var renderDiskPath = Environment.GetEnvironmentVariable("RENDER_DISK_PATH");

string connectionString;
if (!string.IsNullOrWhiteSpace(explicitDbPath))
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

builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));
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
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".keys")))
    .SetApplicationName("KategoriSecici");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    var dbConnection = db.Database.GetDbConnection();
    dbConnection.Open();
    using (var cmd = dbConnection.CreateCommand())
    {
        cmd.CommandText = "PRAGMA table_info('MedyaOgeleri');";
        var hasIzlendi = false;
        var hasAppUserId = false;
        {
            using var reader = cmd.ExecuteReader();
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

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS AppUsers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserName TEXT NOT NULL DEFAULT '',
                Email TEXT NOT NULL,
                DisplayName TEXT NULL,
                PasswordHash TEXT NULL,
                AuthProvider TEXT NOT NULL DEFAULT 'local',
                GoogleSubject TEXT NULL,
                CoverImagePath TEXT NULL,
                AvatarImagePath TEXT NULL,
                CreatedAt TEXT NOT NULL
            );
            """);
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_AppUsers_Email ON AppUsers (Email);");

        using var userCmd = dbConnection.CreateCommand();
        userCmd.CommandText = "PRAGMA table_info('AppUsers');";
        var hasUserName = false;
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
        }

        db.SaveChanges();
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_AppUsers_UserName ON AppUsers (UserName);");
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
