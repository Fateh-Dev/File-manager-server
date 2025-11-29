using FileManager.API.Data;
using FileManager.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileManager.API.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class PermissionsController : ControllerBase
{
    private readonly AppDbContext _context;

    public PermissionsController(AppDbContext context)
    {
        _context = context;
    }

    private int GetUserId() => int.Parse(User.FindFirst("id")!.Value);

    [HttpPost("grant")]
    public async Task<IActionResult> GrantPermission(GrantPermissionDto dto)
    {
        var currentUserId = GetUserId();

        // Verify ownership
        if (dto.FolderId.HasValue)
        {
            var folder = await _context.Folders.FindAsync(dto.FolderId);
            if (folder == null || folder.OwnerId != currentUserId) return Forbid();
        }
        else if (dto.FileId.HasValue)
        {
            var file = await _context.Files.FindAsync(dto.FileId);
            if (file == null || file.OwnerId != currentUserId) return Forbid();
        }
        else
        {
            return BadRequest("FolderId or FileId must be provided");
        }

        var permission = new Permission
        {
            UserId = dto.UserId,
            FolderId = dto.FolderId,
            FileId = dto.FileId,
            AccessLevel = dto.AccessLevel
        };

        _context.Permissions.Add(permission);
        await _context.SaveChangesAsync();

        return Ok(permission);
    }
}

public class GrantPermissionDto
{
    public int UserId { get; set; }
    public int? FolderId { get; set; }
    public int? FileId { get; set; }
    public AccessLevel AccessLevel { get; set; }
}
