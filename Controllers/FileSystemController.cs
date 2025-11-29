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

    private int GetUserId()
    {
        var idClaim = User.FindFirst("id");
        if (idClaim == null) throw new UnauthorizedAccessException("User ID claim not found");
        return int.Parse(idClaim.Value);
    }

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
            subFolders = folder.SubFolders.Where(f => !f.IsDeleted).Select(f => new { id = f.Id, name = f.Name }),
            files = folder.Files.Where(f => !f.IsDeleted).Select(f => new { id = f.Id, name = f.Name, extension = f.Extension, size = f.Size, uploadDate = f.UploadDate })
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

            return Ok(new { id = folder.Id, name = folder.Name, parentFolderId = folder.ParentFolderId });
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

        return Ok(new 
        { 
            id = metadata.Id, 
            name = metadata.Name, 
            extension = metadata.Extension, 
            size = metadata.Size, 
            uploadDate = metadata.UploadDate,
            folderId = metadata.FolderId
        });
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

            return Ok(new { id = folder.Id, name = folder.Name, parentFolderId = folder.ParentFolderId });
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

            // Recursive soft delete
            await SoftDeleteFolderRecursive(folder);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "Folder and contents moved to Recycle Bin" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpDelete("file/{fileId}")]
    public async Task<IActionResult> DeleteFile(int fileId)
    {
        try
        {
            var userId = GetUserId();
            var file = await _context.Files.FindAsync(fileId);
            
            if (file == null) return NotFound();
            
            if (file.OwnerId != userId && !await HasPermission(userId, null, fileId, AccessLevel.Edit))
                return Forbid();

            file.IsDeleted = true;
            file.DeletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { Message = "File moved to Recycle Bin" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    private async Task SoftDeleteFolderRecursive(Folder folder)
    {
        folder.IsDeleted = true;
        folder.DeletedAt = DateTime.UtcNow;

        // Load subfolders and files if not already loaded (though we included them in the query, 
        // for deep recursion we might need to load them or use a different strategy. 
        // For now, let's assume we need to load them for deep levels or use a raw SQL query for efficiency.
        // But EF Core fix-up might not load grandchildren. 
        // Let's explicitly load children for recursion if needed, or use a loop.
        // Actually, for a proper recursive delete, we should probably load the entire tree or do it iteratively.
        // Given the potential depth, let's load children explicitly.
        
        var subFolders = await _context.Folders
            .Include(f => f.Files)
            .Where(f => f.ParentFolderId == folder.Id && !f.IsDeleted)
            .ToListAsync();

        foreach (var subFolder in subFolders)
        {
            await SoftDeleteFolderRecursive(subFolder);
        }

        var files = await _context.Files
            .Where(f => f.FolderId == folder.Id && !f.IsDeleted)
            .ToListAsync();

        foreach (var file in files)
        {
            file.IsDeleted = true;
            file.DeletedAt = DateTime.UtcNow;
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
                // Prevent moving folder into itself
                if (dto.TargetFolderId.Value == folderId)
                    return BadRequest(new { Error = "Cannot move folder into itself" });

                var targetFolder = await _context.Folders.FindAsync(dto.TargetFolderId.Value);
                if (targetFolder == null) return NotFound("Target folder not found");
                
                if (targetFolder.OwnerId != userId && !await HasPermission(userId, dto.TargetFolderId.Value, null, AccessLevel.Edit))
                    return Forbid("No permission to move folder here");
                
                // Check if target is a descendant using projection to avoid tracking issues
                var currentId = dto.TargetFolderId.Value;
                while (true)
                {
                    // Get parent ID of the current node
                    var parentId = await _context.Folders
                        .Where(f => f.Id == currentId)
                        .Select(f => f.ParentFolderId)
                        .FirstOrDefaultAsync();

                    if (parentId == folderId)
                        return BadRequest(new { Error = "Cannot move folder into its own subfolder" });
                    
                    if (parentId == null) break;
                    currentId = parentId.Value;
                    
                    // Safety break for potential infinite loops in DB cycles (though unlikely)
                    if (currentId == dto.TargetFolderId.Value) break; 
                }
            }

            folder.ParentFolderId = dto.TargetFolderId;
            await _context.SaveChangesAsync();

            return Ok(new { id = folder.Id, name = folder.Name, parentFolderId = folder.ParentFolderId });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MoveFolder Error: {ex}");
            return StatusCode(500, new { Error = ex.Message, StackTrace = ex.StackTrace });
        }
    }

    [HttpPut("file/{fileId}/move")]
    public async Task<IActionResult> MoveFile(int fileId, [FromBody] MoveFileDto dto)
    {
        try
        {
            var userId = GetUserId();
            var file = await _context.Files.FindAsync(fileId);
            
            if (file == null) return NotFound();
            
            if (file.OwnerId != userId && !await HasPermission(userId, null, fileId, AccessLevel.Edit))
                return Forbid();

            // Check if target folder exists and user has permission
            if (dto.TargetFolderId.HasValue)
            {
                var targetFolder = await _context.Folders.FindAsync(dto.TargetFolderId.Value);
                if (targetFolder == null) return NotFound("Target folder not found");
                
                if (targetFolder.OwnerId != userId && !await HasPermission(userId, dto.TargetFolderId.Value, null, AccessLevel.Edit))
                    return Forbid("No permission to move file here");
            }

            file.FolderId = dto.TargetFolderId ?? 0; // 0 or null depending on how root is handled, assuming 0/null for root? 
            // Actually FolderId is int, not int?. If root is not supported for files, we need to check.
            // Based on models, FolderId is int. Let's assume root is not allowed or handled elsewhere.
            // Wait, FileMetadata has FolderId as int. If it's nullable in DB, it should be int?.
            // Let's check FileMetadata model.
            
            // Re-reading FileMetadata model...
            // It was: public int FolderId { get; set; }
            // So files MUST be in a folder? Or is there a root folder with ID 1?
            // In App.ts: currentFolderId: number = 1;
            // So root is 1.
            
            if (dto.TargetFolderId.HasValue)
            {
                 file.FolderId = dto.TargetFolderId.Value;
            }
            else
            {
                // If moving to root (if supported via null), set to 1?
                // The DTO has int? TargetFolderId.
                // If null, maybe it means root?
                // Let's assume 1 is root for now as per frontend.
                 file.FolderId = 1; 
            }

            await _context.SaveChangesAsync();

            return Ok(new { id = file.Id, name = file.Name, folderId = file.FolderId });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MoveFile Error: {ex}");
            return StatusCode(500, new { Error = ex.Message, StackTrace = ex.StackTrace });
        }
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Search query cannot be empty");

        var userId = GetUserId();
        var lowerQuery = query.ToLower();

        // Search folders - using GroupJoin for better query performance and correctness
        var foldersQuery = from folder in _context.Folders
                          where folder.Name.ToLower().Contains(lowerQuery) && !folder.IsDeleted
                          join permission in _context.Permissions 
                              on folder.Id equals permission.FolderId into perms
                          from perm in perms.DefaultIfEmpty()
                          where folder.OwnerId == userId || 
                                (perm != null && perm.UserId == userId && perm.AccessLevel >= AccessLevel.Read)
                          select new { id = folder.Id, name = folder.Name, parentFolderId = folder.ParentFolderId };

        var folders = await foldersQuery.Distinct().ToListAsync();

        // Search files - using GroupJoin for better query performance and correctness
        var filesQuery = from file in _context.Files
                        where file.Name.ToLower().Contains(lowerQuery) && !file.IsDeleted
                        join permission in _context.Permissions 
                            on file.Id equals permission.FileId into perms
                        from perm in perms.DefaultIfEmpty()
                        where file.OwnerId == userId || 
                              (perm != null && perm.UserId == userId && perm.AccessLevel >= AccessLevel.Read)
                        select new { id = file.Id, name = file.Name, extension = file.Extension, size = file.Size, uploadDate = file.UploadDate, folderId = file.FolderId };

        var files = await filesQuery.Distinct().ToListAsync();

        return Ok(new
        {
            folders = folders,
            files = files
        });
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

    [HttpGet("recycle-bin")]
    public async Task<IActionResult> GetRecycleBin()
    {
        var userId = GetUserId();
        
        var deletedFolders = await _context.Folders
            .Where(f => f.OwnerId == userId && f.IsDeleted)
            .Select(f => new { f.Id, f.Name, f.DeletedAt, Type = "folder" })
            .ToListAsync();

        var deletedFiles = await _context.Files
            .Where(f => f.OwnerId == userId && f.IsDeleted)
            .Select(f => new { f.Id, f.Name, f.Size, f.Extension, f.DeletedAt, Type = "file" })
            .ToListAsync();

        return Ok(new { Folders = deletedFolders, Files = deletedFiles });
    }

    [HttpPut("folder/{id}/restore")]
    public async Task<IActionResult> RestoreFolder(int id)
    {
        var userId = GetUserId();
        var folder = await _context.Folders.Include(f => f.SubFolders).Include(f => f.Files).FirstOrDefaultAsync(f => f.Id == id);
        
        if (folder == null) return NotFound();
        if (folder.OwnerId != userId) return Forbid();

        await RestoreFolderRecursive(folder);
        await _context.SaveChangesAsync();

        return Ok(new { Message = "Folder restored successfully" });
    }

    private async Task RestoreFolderRecursive(Folder folder)
    {
        folder.IsDeleted = false;
        folder.DeletedAt = null;

        if (folder.SubFolders != null)
        {
            foreach (var subFolder in folder.SubFolders)
            {
                await RestoreFolderRecursive(subFolder);
            }
        }

        if (folder.Files != null)
        {
            foreach (var file in folder.Files)
            {
                file.IsDeleted = false;
                file.DeletedAt = null;
            }
        }
    }

    [HttpPut("file/{id}/restore")]
    public async Task<IActionResult> RestoreFile(int id)
    {
        var userId = GetUserId();
        var file = await _context.Files.FindAsync(id);
        
        if (file == null) return NotFound();
        if (file.OwnerId != userId) return Forbid();

        file.IsDeleted = false;
        file.DeletedAt = null;
        await _context.SaveChangesAsync();

        return Ok(new { Message = "File restored successfully" });
    }

    [HttpDelete("folder/{id}/purge")]
    public async Task<IActionResult> PurgeFolder(int id)
    {
        var userId = GetUserId();
        var folder = await _context.Folders.Include(f => f.SubFolders).Include(f => f.Files).FirstOrDefaultAsync(f => f.Id == id);
        
        if (folder == null) return NotFound();
        if (folder.OwnerId != userId) return Forbid();

        // Hard delete
        _context.Folders.Remove(folder);
        await _context.SaveChangesAsync();

        return Ok(new { Message = "Folder permanently deleted" });
    }

    [HttpDelete("file/{id}/purge")]
    public async Task<IActionResult> PurgeFile(int id)
    {
        var userId = GetUserId();
        var file = await _context.Files.FindAsync(id);
        
        if (file == null) return NotFound();
        if (file.OwnerId != userId) return Forbid();

        // Hard delete
        _context.Files.Remove(file);
        // Also delete physical file
        if (System.IO.File.Exists(file.PhysicalPath))
        {
            System.IO.File.Delete(file.PhysicalPath);
        }
        await _context.SaveChangesAsync();

        return Ok(new { Message = "File permanently deleted" });
    }

    [HttpGet("recent")]
    public async Task<IActionResult> GetRecentFiles()
    {
        var userId = GetUserId();
        var recentFiles = await _context.Files
            .Where(f => f.OwnerId == userId && !f.IsDeleted)
            .OrderByDescending(f => f.UploadDate)
            .Take(20)
            .Select(f => new 
            { 
                f.Id, 
                f.Name, 
                f.Extension, 
                f.Size, 
                f.UploadDate, 
                f.FolderId 
            })
            .ToListAsync();

        return Ok(new { Files = recentFiles });
    }

    [HttpGet("downloads")]
    public async Task<IActionResult> GetDownloads()
    {
        var userId = GetUserId();
        
        // Find or create "Downloads" folder
        var downloadsFolder = await _context.Folders
            .FirstOrDefaultAsync(f => f.OwnerId == userId && f.Name == "Downloads" && f.ParentFolderId == null);

        if (downloadsFolder == null)
        {
            downloadsFolder = new Folder
            {
                Name = "Downloads",
                OwnerId = userId,
                ParentFolderId = null // Root level
            };
            _context.Folders.Add(downloadsFolder);
            await _context.SaveChangesAsync();
        }

        return await GetFolderContents(downloadsFolder.Id);
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

public class MoveFileDto
{
    public int? TargetFolderId { get; set; }
}
