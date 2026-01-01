using FileManager.API.Models;
using Microsoft.EntityFrameworkCore;

namespace FileManager.API.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<Folder> Folders { get; set; }
    public DbSet<FileMetadata> Files { get; set; }
    public DbSet<Permission> Permissions { get; set; }
    public DbSet<SharedLink> SharedLinks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        modelBuilder.Entity<SharedLink>()
            .HasIndex(s => s.Token)
            .IsUnique();
    }
}
