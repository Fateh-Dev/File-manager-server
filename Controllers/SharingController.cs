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
public class SharingController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IFileStorageService _fileStorage;

    public SharingController(AppDbContext context, IFileStorageService fileStorage)
    {
        _context = context;
        _fileStorage = fileStorage;
    }

    private int? GetUserId()
    {
        var idClaim = User.FindFirst("id");
        return idClaim != null ? int.Parse(idClaim.Value) : null;
    }

    [HttpPost("links")]
    [Authorize]
    public async Task<IActionResult> CreateShareLink(CreateShareLinkDto dto)
    {
        var userId = GetUserId()!.Value;

        // Verify ownership
        if (dto.FileId.HasValue)
        {
            var file = await _context.Files.FindAsync(dto.FileId.Value);
            if (file == null || file.OwnerId != userId) return Forbid();
        }
        else if (dto.FolderId.HasValue)
        {
            var folder = await _context.Folders.FindAsync(dto.FolderId.Value);
            if (folder == null || folder.OwnerId != userId) return Forbid();
        }
        else
        {
            return BadRequest("FileId or FolderId must be provided");
        }

        var token = Guid.NewGuid().ToString("n")[..12]; // Short token

        var sharedLink = new SharedLink
        {
            Token = token,
            FileId = dto.FileId,
            FolderId = dto.FolderId,
            CreatorId = userId,
            ExpirationDate = dto.ExpirationDate,
            CreatedAt = DateTime.UtcNow
        };

        _context.SharedLinks.Add(sharedLink);
        await _context.SaveChangesAsync();

        return Ok(sharedLink);
    }

    [HttpGet("info/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSharedInfo(string token)
    {
        var sharedLink = await _context.SharedLinks
            .Include(s => s.File)
            .Include(s => s.Folder)
                .ThenInclude(f => f!.SubFolders)
            .Include(s => s.Folder)
                .ThenInclude(f => f!.Files)
            .FirstOrDefaultAsync(s => s.Token == token);

        if (sharedLink == null) return NotFound("Link not found");

        if (sharedLink.ExpirationDate.HasValue && sharedLink.ExpirationDate < DateTime.UtcNow)
        {
            return BadRequest("This link has expired");
        }

        if (sharedLink.FileId.HasValue)
        {
            if (sharedLink.File == null || sharedLink.File.IsDeleted) return NotFound("File no longer available");
            return Ok(new
            {
                type = "file",
                name = sharedLink.File.Name,
                size = sharedLink.File.Size,
                extension = sharedLink.File.Extension,
                uploadDate = sharedLink.File.UploadDate
            });
        }
        else if (sharedLink.FolderId.HasValue)
        {
            if (sharedLink.Folder == null || sharedLink.Folder.IsDeleted) return NotFound("Folder no longer available");
            return Ok(new
            {
                type = "folder",
                name = sharedLink.Folder.Name,
                subFolders = sharedLink.Folder.SubFolders.Where(f => !f.IsDeleted).Select(f => new { id = f.Id, name = f.Name }),
                files = sharedLink.Folder.Files.Where(f => !f.IsDeleted).Select(f => new { id = f.Id, name = f.Name, extension = f.Extension, size = f.Size })
            });
        }

        return BadRequest("Invalid shared link");
    }

    [HttpGet("download/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> DownloadSharedFile(string token)
    {
        var sharedLink = await _context.SharedLinks
            .Include(s => s.File)
            .FirstOrDefaultAsync(s => s.Token == token);

        if (sharedLink == null || !sharedLink.FileId.HasValue) return NotFound();

        if (sharedLink.ExpirationDate.HasValue && sharedLink.ExpirationDate < DateTime.UtcNow)
        {
            return BadRequest("Link expired");
        }

        var file = sharedLink.File;
        if (file == null || file.IsDeleted) return NotFound();

        var stream = await _fileStorage.GetFileAsync(file.PhysicalPath);
        return File(stream, "application/octet-stream", file.Name);
    }

    [HttpGet("my-links")]
    [Authorize]
    public async Task<IActionResult> GetMyLinks()
    {
        var userId = GetUserId()!.Value;
        var links = await _context.SharedLinks
            .Where(l => l.CreatorId == userId)
            .Include(l => l.File)
            .Include(l => l.Folder)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

        return Ok(links.Select(l => new {
            l.Id,
            l.Token,
            l.CreatedAt,
            l.ExpirationDate,
            itemName = l.File?.Name ?? l.Folder?.Name,
            type = l.FileId.HasValue ? "file" : "folder"
        }));
    }

    [HttpDelete("links/{id}")]
    [Authorize]
    public async Task<IActionResult> RevokeLink(int id)
    {
        var userId = GetUserId()!.Value;
        var link = await _context.SharedLinks.FindAsync(id);

        if (link == null) return NotFound();
        if (link.CreatorId != userId) return Forbid();

        _context.SharedLinks.Remove(link);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Link revoked successfully" });
    }
}

public class CreateShareLinkDto
{
    public int? FileId { get; set; }
    public int? FolderId { get; set; }
    public DateTime? ExpirationDate { get; set; }
}
