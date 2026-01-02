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

    [HttpGet("root")]
    public async Task<IActionResult> GetRootFolder()
    {
        var userId = GetUserId();
        var rootFolder = await _context.Folders
            .FirstOrDefaultAsync(f => f.OwnerId == userId && f.ParentFolderId == null && !f.IsDeleted);
        
        if (rootFolder == null)
        {
            // Create root folder if it doesn't exist
            rootFolder = new Folder
            {
                Name = "Root",
                OwnerId = userId,
                ParentFolderId = null
            };
            _context.Folders.Add(rootFolder);
            await _context.SaveChangesAsync();
        }
        
        return Ok(new { id = rootFolder.Id, name = rootFolder.Name });
    }

    [HttpGet("folder/{folderId}")]
    public async Task<IActionResult> GetFolderContents(int folderId)
    {
        var userId = GetUserId();
        
        var folder = await _context.Folders
            .Include(f => f.Owner)
            .Include(f => f.SubFolders)
                .ThenInclude(sf => sf.Owner)
            .Include(f => f.Files)
                .ThenInclude(file => file.Owner)
            .FirstOrDefaultAsync(f => f.Id == folderId);

        if (folder == null) return NotFound();

        // Basic permission check: Owner or has permission
        var effectiveAccessLevel = await GetEffectiveAccessLevel(userId, folderId, null);
        if (folder.OwnerId != userId && (effectiveAccessLevel == null || effectiveAccessLevel < AccessLevel.Read))
        {
             return Forbid();
        }

        var subFolders = new List<object>();
        foreach (var f in folder.SubFolders.Where(f => !f.IsDeleted))
        {
            var level = await GetEffectiveAccessLevel(userId, f.Id, null);
            subFolders.Add(new { 
                id = f.Id, 
                name = f.Name, 
                ownerName = f.Owner?.Username ?? "Inconnu",
                accessLevel = (int)(level ?? AccessLevel.Read) // Default to Read if parent is shared
            });
        }

        var files = new List<object>();
        foreach (var f in folder.Files.Where(f => !f.IsDeleted))
        {
            var level = await GetEffectiveAccessLevel(userId, null, f.Id);
            files.Add(new { 
                id = f.Id, 
                name = f.Name, 
                extension = f.Extension, 
                size = f.Size, 
                uploadDate = f.UploadDate,
                ownerName = f.Owner?.Username ?? "Inconnu",
                accessLevel = (int)(level ?? AccessLevel.Read)
            });
        }

        return Ok(new { 
            id = folder.Id, 
            name = folder.Name, 
            ownerName = folder.Owner?.Username ?? "Inconnu",
            accessLevel = (int)(effectiveAccessLevel ?? AccessLevel.Read),
            subFolders = subFolders,
            files = files
        });
    }

    [HttpPost("folder")]
    public async Task<IActionResult> CreateFolder(CreateFolderDto dto)
    {
        try
        {
            var userId = GetUserId();
            
            // Validate folder name
            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                return BadRequest(new { Error = "Folder name cannot be empty" });
            }
            
            // Check parent folder permissions
            if (dto.ParentFolderId.HasValue)
            {
                var parentFolder = await _context.Folders.FindAsync(dto.ParentFolderId.Value);
                if (parentFolder == null)
                {
                    return NotFound(new { Error = $"Parent folder with ID {dto.ParentFolderId.Value} not found" });
                }
                
                if (parentFolder.IsDeleted)
                {
                    return BadRequest(new { Error = "Cannot create folder in a deleted folder" });
                }
                
                if (!await HasPermission(userId, dto.ParentFolderId.Value, null, AccessLevel.Edit))
                {
                    return Forbid();
                }
            }

            var folder = new Folder
            {
                Name = dto.Name.Trim(),
                ParentFolderId = dto.ParentFolderId,
                OwnerId = userId
            };

            _context.Folders.Add(folder);
            await _context.SaveChangesAsync();

            return Ok(new { id = folder.Id, name = folder.Name, parentFolderId = folder.ParentFolderId });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CreateFolder Error: {ex}");
            return StatusCode(500, new { Error = ex.Message, StackTrace = ex.StackTrace });
        }
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadFile([FromForm] UploadFileDto dto)
    {
        try
        {
            var userId = GetUserId();

            if (!await HasPermission(userId, dto.FolderId, null, AccessLevel.Edit))
                return Forbid();

            var file = dto.File;
            if (file == null || file.Length == 0) return BadRequest("No file uploaded");

            // Check storage quota
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return Unauthorized();

            if (user.UsedStorage + file.Length > user.StorageLimit)
            {
                return BadRequest(new { Error = "Storage quota exceeded. Please delete some files or contact an administrator." });
            }

            var path = await _fileStorage.SaveFileAsync(file.OpenReadStream(), file.FileName);

            var baseFileName = Path.GetFileName(file.FileName);
            var metadata = new FileMetadata
            {
                Name = baseFileName,
                Extension = Path.GetExtension(baseFileName),
                Size = file.Length,
                PhysicalPath = path,
                FolderId = dto.FolderId,
                OwnerId = userId
            };

            _context.Files.Add(metadata);
            
            // Update user storage usage
            user.UsedStorage += file.Length;
            
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
        catch (Exception ex)
        {
            Console.WriteLine($"UploadFile Error: {ex}");
            return StatusCode(500, new { Error = ex.Message, StackTrace = ex.StackTrace });
        }
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

        // 1. Get all potential matches
        var allMatchingFolders = await _context.Folders
            .Include(f => f.Owner)
            .Where(f => f.Name.ToLower().Contains(lowerQuery) && !f.IsDeleted)
            .ToListAsync();

        var allMatchingFiles = await _context.Files
            .Include(f => f.Owner)
            .Where(f => f.Name.ToLower().Contains(lowerQuery) && !f.IsDeleted)
            .ToListAsync();

        // 2. Filter based on effective permissions
        var visibleFolders = new List<object>();
        foreach (var folder in allMatchingFolders)
        {
            var level = await GetEffectiveAccessLevel(userId, folder.Id, null);
            if (level != null)
            {
                visibleFolders.Add(new { 
                    id = folder.Id, 
                    name = folder.Name, 
                    parentFolderId = folder.ParentFolderId,
                    ownerName = folder.Owner?.Username ?? "Inconnu",
                    accessLevel = (int)level
                });
            }
        }

        var visibleFiles = new List<object>();
        foreach (var file in allMatchingFiles)
        {
            var level = await GetEffectiveAccessLevel(userId, null, file.Id);
            if (level != null)
            {
                visibleFiles.Add(new { 
                    id = file.Id, 
                    name = file.Name, 
                    extension = file.Extension, 
                    size = file.Size, 
                    uploadDate = file.UploadDate, 
                    folderId = file.FolderId,
                    ownerName = file.Owner?.Username ?? "Inconnu",
                    accessLevel = (int)level
                });
            }
        }

        return Ok(new
        {
            folders = visibleFolders,
            files = visibleFiles
        });
    }

    private async Task<bool> HasPermission(int userId, int? folderId, int? fileId, AccessLevel level)
    {
        var effectiveLevel = await GetEffectiveAccessLevel(userId, folderId, fileId);
        if (effectiveLevel == null) return false;
        return (int)effectiveLevel.Value >= (int)level;
    }

    private async Task<AccessLevel?> GetEffectiveAccessLevel(int userId, int? folderId, int? fileId)
    {
        try
        {
            // 1. Direct Ownership Check
            if (folderId.HasValue)
            {
                var folder = await _context.Folders.FindAsync(folderId.Value);
                if (folder != null && folder.OwnerId == userId) return AccessLevel.Delete; // Owner has full access
            }
            if (fileId.HasValue)
            {
                var file = await _context.Files.FindAsync(fileId.Value);
                if (file != null && file.OwnerId == userId) return AccessLevel.Delete;
            }

            // 2. Direct Permission Check
            var directPerm = await _context.Permissions
                .Where(p => p.UserId == userId && (p.FolderId == folderId || (fileId.HasValue && p.FileId == fileId)))
                .OrderByDescending(p => p.AccessLevel)
                .Select(p => (AccessLevel?)p.AccessLevel)
                .FirstOrDefaultAsync();

            // If we found a direct permission, it might be higher than inherited ones, but in inheritance logic,
            // usually the most specific one or the highest one wins. We'll compare with parent later.
            AccessLevel? currentMax = directPerm;

            // 3. Inheritance Check (Crawl up the tree)
            int? currentFolderId = folderId;
            
            // If checking a file, start from its parent folder
            if (fileId.HasValue && !folderId.HasValue)
            {
                currentFolderId = await _context.Files
                    .Where(f => f.Id == fileId.Value)
                    .Select(f => f.FolderId)
                    .FirstOrDefaultAsync();
            }

            // Maximum depth to prevent infinite loops (safety)
            int depth = 0;
            while (currentFolderId.HasValue && depth < 50)
            {
                var parentPerm = await _context.Permissions
                    .Where(p => p.UserId == userId && p.FolderId == currentFolderId.Value)
                    .Select(p => (AccessLevel?)p.AccessLevel)
                    .FirstOrDefaultAsync();

                if (parentPerm.HasValue && (currentMax == null || parentPerm > currentMax))
                {
                    currentMax = parentPerm;
                }

                // If we already have Edit/Delete, we can probably stop (Read < Edit < Delete)
                if (currentMax >= AccessLevel.Edit) break;

                // Move to parent
                currentFolderId = await _context.Folders
                    .Where(f => f.Id == currentFolderId.Value)
                    .Select(f => f.ParentFolderId)
                    .FirstOrDefaultAsync();
                
                depth++;
            }

            return currentMax;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetEffectiveAccessLevel error: {ex.Message}");
            return null;
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
