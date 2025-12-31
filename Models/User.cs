using System.ComponentModel.DataAnnotations;

namespace FileManager.API.Models;

public class User
{
    public int Id { get; set; }
    [Required]
    public string Username { get; set; } = string.Empty;
    [Required]
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "User"; // Admin, User
    public bool IsActive { get; set; } = false;
    public long StorageLimit { get; set; } = 5368709120; // 5 GB
    public long UsedStorage { get; set; } = 0;
}
