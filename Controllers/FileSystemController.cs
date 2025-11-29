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
public class FileSystemController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IFileStorageService _fileStorage;

    public FileSystemController(AppDbContext context, IFileStorageService fileStorage)
    {
        _context = context;
        _fileStorage = fileStorage;
    }

    private int GetUserId() => int.Parse(User.FindFirst("id")!.Value);

    [HttpGet("folder/{folderId}")]
    public async Task<IActionResult> GetFolderContents(int folderId)
    {
        var userId = GetUserId();
        
        var folder = await _context.Folders
            .Include(f => f.SubFolders)
            .Include(f => f.Files)
            .FirstOrDefaultAsync(f => f.Id == folderId);

        if (folder == null) return NotFound();

        // Basic permission check: Owner or has permission
        if (folder.OwnerId != userId && !await HasPermission(userId, folderId, null, AccessLevel.Read))
        {
             return Forbid();
        }

        return Ok(new { 
            id = folder.Id, 
            name = folder.Name, 
            subFolders = folder.SubFolders.Select(f => new { id = f.Id, name = f.Name }),
            files = folder.Files.Select(f => new { id = f.Id, name = f.Name, extension = f.Extension, size = f.Size, uploadDate = f.UploadDate })
        });
    }

    [HttpPost("folder")]
    public async Task<IActionResult> CreateFolder(CreateFolderDto dto)
    {
        try
        {
            var userId = GetUserId();
            
            // Check parent folder permissions
            if (dto.ParentFolderId.HasValue)
            {
                 if (!await HasPermission(userId, dto.ParentFolderId.Value, null, AccessLevel.Edit))
                     return Forbid();
            }

            var folder = new Folder
            {
                Name = dto.Name,
                ParentFolderId = dto.ParentFolderId,
                OwnerId = userId
            };

            _context.Folders.Add(folder);
            await _context.SaveChangesAsync();

            return Ok(folder);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message, StackTrace = ex.StackTrace });
        }
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadFile([FromForm] UploadFileDto dto)
    {
        var userId = GetUserId();

        if (!await HasPermission(userId, dto.FolderId, null, AccessLevel.Edit))
            return Forbid();

        var file = dto.File;
        if (file == null || file.Length == 0) return BadRequest("No file uploaded");

        var path = await _fileStorage.SaveFileAsync(file.OpenReadStream(), file.FileName);

        var metadata = new FileMetadata
        {
            Name = file.FileName,
            Extension = Path.GetExtension(file.FileName),
            Size = file.Length,
            PhysicalPath = path,
            FolderId = dto.FolderId,
            OwnerId = userId
        };

        _context.Files.Add(metadata);
        await _context.SaveChangesAsync();

        return Ok(metadata);
    }

    [HttpGet("download/{fileId}")]
    public async Task<IActionResult> DownloadFile(int fileId)
    {
        var userId = GetUserId();
        var file = await _context.Files.FindAsync(fileId);
        if (file == null) return NotFound();

        if (file.OwnerId != userId && !await HasPermission(userId, null, fileId, AccessLevel.Read))
            return Forbid();

        var stream = await _fileStorage.GetFileAsync(file.PhysicalPath);
        return File(stream, "application/octet-stream", file.Name);
    }

    [HttpPut("folder/{folderId}/rename")]
    public async Task<IActionResult> RenameFolder(int folderId, [FromBody] RenameFolderDto dto)
    {
        try
        {
            var userId = GetUserId();
            var folder = await _context.Folders.FindAsync(folderId);
            
            if (folder == null) return NotFound();
            
            if (folder.OwnerId != userId && !await HasPermission(userId, folderId, null, AccessLevel.Edit))
                return Forbid();

            folder.Name = dto.Name;
            await _context.SaveChangesAsync();

            return Ok(folder);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpDelete("folder/{folderId}")]
    public async Task<IActionResult> DeleteFolder(int folderId)
    {
        try
        {
            var userId = GetUserId();
            var folder = await _context.Folders
                .Include(f => f.SubFolders)
                .Include(f => f.Files)
                .FirstOrDefaultAsync(f => f.Id == folderId);
            
            if (folder == null) return NotFound();
            
            if (folder.OwnerId != userId && !await HasPermission(userId, folderId, null, AccessLevel.Edit))
                return Forbid();

            // Check if folder has subfolders or files
            if (folder.SubFolders != null && folder.SubFolders.Any())
                return BadRequest(new { Error = "Cannot delete folder with subfolders. Please delete subfolders first." });
            
            if (folder.Files != null && folder.Files.Any())
                return BadRequest(new { Error = "Cannot delete folder with files. Please delete files first." });

            _context.Folders.Remove(folder);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "Folder deleted successfully" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpPut("folder/{folderId}/move")]
    public async Task<IActionResult> MoveFolder(int folderId, [FromBody] MoveFolderDto dto)
    {
        try
        {
            var userId = GetUserId();
            var folder = await _context.Folders.FindAsync(folderId);
            
            if (folder == null) return NotFound();
            
            if (folder.OwnerId != userId && !await HasPermission(userId, folderId, null, AccessLevel.Edit))
                return Forbid();

            // Check if target folder exists and user has permission
            if (dto.TargetFolderId.HasValue)
            {
                var targetFolder = await _context.Folders.FindAsync(dto.TargetFolderId.Value);
                if (targetFolder == null) return NotFound("Target folder not found");
                
                if (targetFolder.OwnerId != userId && !await HasPermission(userId, dto.TargetFolderId.Value, null, AccessLevel.Edit))
                    return Forbid("No permission to move folder here");
                
                // Prevent moving folder into itself or its descendants
                if (dto.TargetFolderId.Value == folderId)
                    return BadRequest(new { Error = "Cannot move folder into itself" });
                
                // Check if target is a descendant
                var current = await _context.Folders.FindAsync(dto.TargetFolderId.Value);
                while (current != null && current.ParentFolderId.HasValue)
                {
                    if (current.ParentFolderId.Value == folderId)
                        return BadRequest(new { Error = "Cannot move folder into its own subfolder" });
                    current = await _context.Folders.FindAsync(current.ParentFolderId.Value);
                }
            }

            folder.ParentFolderId = dto.TargetFolderId;
            await _context.SaveChangesAsync();

            return Ok(folder);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    private async Task<bool> HasPermission(int userId, int? folderId, int? fileId, AccessLevel level)
    {
        try
        {
            // Check ownership first
            if (folderId.HasValue)
            {
                var folder = await _context.Folders.FindAsync(folderId.Value);
                if (folder != null && folder.OwnerId == userId) return true;
            }
            if (fileId.HasValue)
            {
                var file = await _context.Files.FindAsync(fileId.Value);
                if (file != null && file.OwnerId == userId) return true;
            }

            // Check explicit permissions
            return await _context.Permissions.AnyAsync(p => 
                p.UserId == userId && 
                (p.FolderId == folderId || p.FileId == fileId) && 
                p.AccessLevel >= level);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HasPermission error: {ex.Message}");
            throw;
        }
    }
}

public class CreateFolderDto
{
    public string Name { get; set; } = string.Empty;
    public int? ParentFolderId { get; set; }
}

public class UploadFileDto
{
    public required IFormFile File { get; set; }
    public int FolderId { get; set; }
}

public class RenameFolderDto
{
    public string Name { get; set; } = string.Empty;
}

public class MoveFolderDto
{
    public int? TargetFolderId { get; set; }
}
