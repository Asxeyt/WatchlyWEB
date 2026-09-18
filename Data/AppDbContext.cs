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
    public DbSet<CatalogWork> CatalogWorks => Set<CatalogWork>();
    public DbSet<CatalogExternalId> CatalogExternalIds => Set<CatalogExternalId>();
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

        modelBuilder.Entity<CatalogWork>(entity =>
        {
            entity.HasIndex(x => new { x.Kategori, x.NormalizedTitle, x.ReleaseYear });
        });

        modelBuilder.Entity<CatalogExternalId>(entity =>
        {
            entity.HasIndex(x => new { x.Kategori, x.Source, x.ExternalId }).IsUnique();
            entity.HasOne(x => x.CatalogWork)
                .WithMany(x => x.ExternalIds)
                .HasForeignKey(x => x.CatalogWorkId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
