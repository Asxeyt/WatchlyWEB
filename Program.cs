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

builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));
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
                Email TEXT NOT NULL,
                DisplayName TEXT NULL,
                PasswordHash TEXT NULL,
                AuthProvider TEXT NOT NULL DEFAULT 'local',
                GoogleSubject TEXT NULL,
                CreatedAt TEXT NOT NULL
            );
            """);
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_AppUsers_Email ON AppUsers (Email);");
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
