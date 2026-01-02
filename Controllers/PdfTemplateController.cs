using FileManager.API.Data;
using FileManager.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileManager.API.Controllers;

public class SaveTemplateDto
{
    public string Name { get; set; } = string.Empty;
    public IFormFile Image { get; set; } = null!;
    public string FieldsJson { get; set; } = string.Empty;
}

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class PdfTemplateController : ControllerBase
{
    private readonly AppDbContext _context;

    public PdfTemplateController(AppDbContext context)
    {
        _context = context;
    }

    [HttpPost]
    public async Task<IActionResult> SaveTemplate([FromForm] SaveTemplateDto dto)
    {
        if (string.IsNullOrEmpty(dto.Name) || dto.Image == null || string.IsNullOrEmpty(dto.FieldsJson))
        {
            return BadRequest("Name, image and fields are required.");
        }

        using var memoryStream = new MemoryStream();
        await dto.Image.CopyToAsync(memoryStream);

        var template = new PdfTemplate
        {
            Name = dto.Name,
            ImageData = memoryStream.ToArray(),
            FieldsJson = dto.FieldsJson,
            CreatedAt = DateTime.UtcNow
        };

        _context.PdfTemplates.Add(template);
        await _context.SaveChangesAsync();

        return Ok(new { id = template.Id, message = "Template saved successfully" });
    }

    [HttpGet]
    public async Task<IActionResult> GetTemplates()
    {
        var templates = await _context.PdfTemplates
            .Select(t => new { t.Id, t.Name, t.CreatedAt })
            .ToListAsync();
        return Ok(templates);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTemplate(int id)
    {
        var template = await _context.PdfTemplates.FindAsync(id);
        if (template == null) return NotFound();

        return Ok(new
        {
            template.Id,
            template.Name,
            template.FieldsJson,
            ImageData = Convert.ToBase64String(template.ImageData)
        });
    }
}
