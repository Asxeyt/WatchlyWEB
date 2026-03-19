using System.IO;
using KategoriSecici.Data;
using Microsoft.AspNetCore.DataProtection;
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
        using var reader = cmd.ExecuteReader();
        var hasIzlendi = false;
        while (reader.Read())
        {
            if (string.Equals(reader["name"]?.ToString(), "Izlendi", StringComparison.OrdinalIgnoreCase))
            {
                hasIzlendi = true;
                break;
            }
        }

        if (!hasIzlendi)
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE MedyaOgeleri ADD COLUMN Izlendi INTEGER NOT NULL DEFAULT 0;");
        }
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

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Anasayfa}/{id?}");

app.Run();
