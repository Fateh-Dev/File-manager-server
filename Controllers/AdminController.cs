using FileManager.API.Data;
using FileManager.API.Models;
using FileManager.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;

namespace FileManager.API.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IAuthService _authService;

    public AdminController(AppDbContext context, IAuthService authService)
    {
        _context = context;
        _authService = authService;
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers()
    {
        try
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
        catch (Exception ex)
        {
            // Log the exception (omitted for brevity)
            return StatusCode(500, new { Message = "Error retrieving users", Details = ex.Message });
        }
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

    [HttpPut("users/{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordDto dto)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound("User not found");

        if (string.IsNullOrEmpty(dto.NewPassword) || dto.NewPassword.Length < 4)
        {
            return BadRequest("Password must be at least 4 characters");
        }

        user.PasswordHash = _authService.HashPassword(dto.NewPassword);
        await _context.SaveChangesAsync();

        return Ok(new { Message = "Password reset successfully" });
    }
}

public class UpdateStorageDto
{
    public long NewLimit { get; set; }
}

public class ResetPasswordDto
{
    public string NewPassword { get; set; } = string.Empty;
}

