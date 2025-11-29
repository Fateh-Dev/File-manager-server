using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FileManager.API.Models;

public class Folder
{
    public int Id { get; set; }
    [Required]
    public string Name { get; set; } = string.Empty;
    public int? ParentFolderId { get; set; }
    [ForeignKey("ParentFolderId")]
    public Folder? ParentFolder { get; set; }
    public int OwnerId { get; set; }
    public User? Owner { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Folder> SubFolders { get; set; } = new List<Folder>();
    public ICollection<FileMetadata> Files { get; set; } = new List<FileMetadata>();
}
