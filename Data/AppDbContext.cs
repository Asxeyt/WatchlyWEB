using KategoriSecici.Models;
using Microsoft.EntityFrameworkCore;

namespace KategoriSecici.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<MedyaOgesi> MedyaOgeleri => Set<MedyaOgesi>();
}
