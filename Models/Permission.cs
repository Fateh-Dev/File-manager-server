using System.ComponentModel.DataAnnotations;

namespace FileManager.API.Models;

public class Permission
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public int? FolderId { get; set; }
    public Folder? Folder { get; set; }
    public int? FileId { get; set; }
    public FileMetadata? File { get; set; }
    public AccessLevel AccessLevel { get; set; }
}

public enum AccessLevel
{
    Read,
    Edit,
    Delete
}
