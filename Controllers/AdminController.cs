using FileManager.API.Data;
using FileManager.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileManager.API.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _context;

    public AdminController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers()
    {
        var users = await _context.Users
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.Role,
                u.IsActive,
                u.StorageLimit,
                u.UsedStorage
            })
            .ToListAsync();
        return Ok(users);
    }

    [HttpPut("users/{id}/activate")]
    public async Task<IActionResult> ActivateUser(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound("User not found");
        }

        user.IsActive = true;
        await _context.SaveChangesAsync();

        return Ok(new { Message = "User activated successfully" });
    }

    [HttpPut("users/{id}/lock")]
    public async Task<IActionResult> LockUser(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound("User not found");
        }

        if (user.Role == "Admin")
        {
             return BadRequest("Cannot lock an Administrator account.");
        }

        user.IsActive = false;
        await _context.SaveChangesAsync();

        return Ok(new { Message = "User account locked" });
    }

    [HttpPut("users/{id}/storage")]
    public async Task<IActionResult> UpdateStorageLimit(int id, [FromBody] UpdateStorageDto dto)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound("User not found");

        if (dto.NewLimit < 0) return BadRequest("Storage limit cannot be negative");

        user.StorageLimit = dto.NewLimit;
        await _context.SaveChangesAsync();

        return Ok(new { Message = "Storage limit updated successfully" });
    }
}

public class UpdateStorageDto
{
    public long NewLimit { get; set; }
}
