using System.ComponentModel.DataAnnotations;

namespace FileManager.API.Models;

public class SharedLink
{
    public int Id { get; set; }

    [Required]
    public string Token { get; set; } = string.Empty;

    public int? FileId { get; set; }
    public FileMetadata? File { get; set; }

    public int? FolderId { get; set; }
    public Folder? Folder { get; set; }

    [Required]
    public int CreatorId { get; set; }
    public User? Creator { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpirationDate { get; set; }
}
