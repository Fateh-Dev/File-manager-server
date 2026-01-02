using System;

namespace FileManager.API.Models;

public class PdfTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte[] ImageData { get; set; } = Array.Empty<byte>();
    public string FieldsJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
