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
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<AppUserMedia> AppUserMedias => Set<AppUserMedia>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUserMedia>(entity =>
        {
            entity.HasKey(x => x.AppUserId);
            entity.Property(x => x.AvatarContentType).HasMaxLength(120);
            entity.Property(x => x.CoverContentType).HasMaxLength(120);
        });
    }
}
