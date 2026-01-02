using FileManager.API.Models;
using Microsoft.EntityFrameworkCore;

namespace FileManager.API.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Folder> Folders { get; set; } = null!;
    public DbSet<FileMetadata> Files { get; set; } = null!;
    public DbSet<Permission> Permissions { get; set; } = null!;
    public DbSet<PdfTemplate> PdfTemplates { get; set; } = null!;
}
