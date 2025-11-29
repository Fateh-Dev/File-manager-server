using System.ComponentModel.DataAnnotations;

namespace FileManager.API.Models;

public class FileMetadata
{
    public int Id { get; set; }
    [Required]
    public string Name { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public string PhysicalPath { get; set; } = string.Empty;
    public int FolderId { get; set; }
    public Folder? Folder { get; set; }
    public int OwnerId { get; set; }
    public User? Owner { get; set; }
    public DateTime UploadDate { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
}
