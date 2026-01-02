using FileManager.API.Data;
using FileManager.API.Models;
using FileManager.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;

namespace FileManager.API.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IAuthService _authService;

    public AuthController(AppDbContext context, IAuthService authService)
    {
        _context = context;
        _authService = authService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        if (await _context.Users.AnyAsync(u => u.Username == dto.Username))
        {
            return BadRequest("Username already exists");
        }

        // Check if this is the first user
        bool isFirstUser = !await _context.Users.AnyAsync();

        var user = new User
        {
            Username = dto.Username,
            PasswordHash = _authService.HashPassword(dto.Password),
            Role = isFirstUser ? "Admin" : "User",
            IsActive = isFirstUser
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Create root folder for user
        var rootFolder = new Folder
        {
            Name = "Root",
            OwnerId = user.Id,
            ParentFolderId = null
        };
        _context.Folders.Add(rootFolder);
        await _context.SaveChangesAsync();

        return Ok(new { Message = "User registered successfully" });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginDto dto)
    {
        try
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == dto.Username);
            if (user == null || !_authService.VerifyPassword(dto.Password, user.PasswordHash))
            {
                return Unauthorized("Invalid credentials");
            }

            if (!user.IsActive)
            {
                return Unauthorized("Account is inactive. Please contact an administrator.");
            }

            var token = _authService.GenerateJwtToken(user);
            return Ok(new { Token = token });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message, StackTrace = ex.StackTrace });
        }
    }
    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return Unauthorized();

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return NotFound();

        return Ok(new
        {
            user.Id,
            user.Username,
            user.Role,
            user.IsActive,
            user.StorageLimit,
            user.UsedStorage
        });
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordDto dto)
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return Unauthorized();

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return NotFound();

        // Verify current password
        if (!_authService.VerifyPassword(dto.CurrentPassword, user.PasswordHash))
        {
            return BadRequest("Current password is incorrect");
        }

        // Update password
        user.PasswordHash = _authService.HashPassword(dto.NewPassword);
        await _context.SaveChangesAsync();

        return Ok(new { Message = "Password changed successfully" });
    }
}

public class RegisterDto
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginDto
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class ChangePasswordDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
