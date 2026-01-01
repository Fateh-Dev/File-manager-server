using FileManager.API.Data;
using FileManager.API.Models;
using FileManager.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace FileManager.API.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class SharingController : ControllerBase
{
    private readonly AppDbContext _context;

    public SharingController(AppDbContext context)
    {
        _context = context;
    }

    private int GetUserId()
    {
        var idClaim = User.FindFirst("id");
        if (idClaim == null)
        {
            // Try claim types name identifier
            idClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        }
        return idClaim != null ? int.Parse(idClaim.Value) : 0;
    }

    [HttpPost("share")]
    public async Task<IActionResult> ShareWithUser(ShareRequestDto dto)
    {
        var currentUserId = GetUserId();

        // 1. Verify ownership
        if (dto.FileId.HasValue)
        {
            var file = await _context.Files.FindAsync(dto.FileId.Value);
            if (file == null) return NotFound("File not found");
            if (file.OwnerId != currentUserId) return Forbid();
        }
        else if (dto.FolderId.HasValue)
        {
            var folder = await _context.Folders.FindAsync(dto.FolderId.Value);
            if (folder == null) return NotFound("Folder not found");
            if (folder.OwnerId != currentUserId) return Forbid();
        }
        else
        {
            return BadRequest("FileId or FolderId must be provided");
        }

        // 2. Clear existing permission if any (to update)
        var existing = await _context.Permissions
            .FirstOrDefaultAsync(p => p.UserId == dto.TargetUserId && 
                                    p.FileId == dto.FileId && 
                                    p.FolderId == dto.FolderId);
        if (existing != null)
        {
            _context.Permissions.Remove(existing);
        }

        // 3. Create new permission
        var permission = new Permission
        {
            UserId = dto.TargetUserId,
            FileId = dto.FileId,
            FolderId = dto.FolderId,
            AccessLevel = dto.AccessLevel
        };

        _context.Permissions.Add(permission);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Item shared successfully" });
    }

    [HttpGet("shared-with-me")]
    public async Task<IActionResult> GetSharedWithMe()
    {
        var userId = GetUserId();

        var sharedItems = await _context.Permissions
            .Where(p => p.UserId == userId)
            .Include(p => p.File)
                .ThenInclude(f => f!.Owner)
            .Include(p => p.Folder)
                .ThenInclude(f => f!.Owner)
            .Select(p => new
            {
                p.Id,
                p.AccessLevel,
                Type = p.FileId.HasValue ? "file" : "folder",
                Item = p.FileId.HasValue ? (object)new {
                    id = p.File!.Id,
                    name = p.File.Name,
                    size = p.File.Size,
                    extension = p.File.Extension,
                    uploadDate = p.File.UploadDate,
                    ownerName = p.File.Owner != null ? p.File.Owner.Username : "Inconnu"
                } : new {
                    id = p.Folder!.Id,
                    name = p.Folder.Name,
                    ownerName = p.Folder.Owner != null ? p.Folder.Owner.Username : "Inconnu"
                }
            })
            .ToListAsync();

        return Ok(sharedItems);
    }

    [HttpGet("item-permissions")]
    public async Task<IActionResult> GetItemPermissions([FromQuery] int? fileId, [FromQuery] int? folderId)
    {
        var currentUserId = GetUserId();

        if (!fileId.HasValue && !folderId.HasValue)
            return BadRequest("FileId or FolderId must be provided");

        // Verify ownership
        if (fileId.HasValue)
        {
            var file = await _context.Files.FindAsync(fileId.Value);
            if (file == null) return NotFound("File not found");
            if (file.OwnerId != currentUserId) return Forbid();
        }
        else if (folderId.HasValue)
        {
            var folder = await _context.Folders.FindAsync(folderId.Value);
            if (folder == null) return NotFound("Folder not found");
            if (folder.OwnerId != currentUserId) return Forbid();
        }

        var permissions = await _context.Permissions
            .Where(p => (fileId.HasValue && p.FileId == fileId) || (folderId.HasValue && p.FolderId == folderId))
            .Include(p => p.User)
            .Select(p => new
            {
                p.Id,
                p.UserId,
                Username = p.User != null ? p.User.Username : "Inconnu",
                p.AccessLevel
            })
            .ToListAsync();

        return Ok(permissions);
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers()
    {
        var currentUserId = GetUserId();
        var users = await _context.Users
            .Where(u => u.Id != currentUserId && u.IsActive)
            .Select(u => new { u.Id, u.Username })
            .ToListAsync();

        return Ok(users);
    }

    [HttpDelete("revoke/{permissionId}")]
    public async Task<IActionResult> RevokeShare(int permissionId)
    {
        var currentUserId = GetUserId();
        var permission = await _context.Permissions
            .Include(p => p.File)
            .Include(p => p.Folder)
            .FirstOrDefaultAsync(p => p.Id == permissionId);

        if (permission == null) return NotFound();

        // Only owner can revoke
        bool isOwner = false;
        if (permission.FileId.HasValue) isOwner = permission.File!.OwnerId == currentUserId;
        else if (permission.FolderId.HasValue) isOwner = permission.Folder!.OwnerId == currentUserId;

        if (!isOwner) return Forbid();

        _context.Permissions.Remove(permission);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Sharing revoked" });
    }
}

public class ShareRequestDto
{
    public int TargetUserId { get; set; }
    public int? FileId { get; set; }
    public int? FolderId { get; set; }
    public AccessLevel AccessLevel { get; set; } = AccessLevel.Read;
}
