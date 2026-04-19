using Microsoft.EntityFrameworkCore;
using QuickRoute.Models;

namespace QuickRoute.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ShortUrl> ShortUrls { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShortUrl>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ShortCode).IsUnique();
            entity.Property(e => e.OriginalUrl).IsRequired();
            entity.Property(e => e.ShortCode).IsRequired();
        });
    }
}