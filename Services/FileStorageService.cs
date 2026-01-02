namespace FileManager.API.Services;

public interface IFileStorageService
{
    Task<string> SaveFileAsync(Stream fileStream, string fileName);
    Task<Stream> GetFileAsync(string physicalPath);
    void DeleteFile(string physicalPath);
}

public class FileStorageService : IFileStorageService
{
    private readonly string _storagePath;

    public FileStorageService(IConfiguration configuration)
    {
        _storagePath = configuration["FileStorage:Path"] ?? "C:\\FileStorage";
        if (!Directory.Exists(_storagePath))
        {
            Directory.CreateDirectory(_storagePath);
        }
    }

    public async Task<string> SaveFileAsync(Stream fileStream, string fileName)
    {
        var baseFileName = Path.GetFileName(fileName);
        var uniqueFileName = $"{Guid.NewGuid()}_{baseFileName}";
        var fullPath = Path.Combine(_storagePath, uniqueFileName);

        using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await fileStream.CopyToAsync(stream);
        }

        return fullPath;
    }

    public async Task<Stream> GetFileAsync(string physicalPath)
    {
        if (!File.Exists(physicalPath))
        {
            throw new FileNotFoundException("File not found", physicalPath);
        }

        return new FileStream(physicalPath, FileMode.Open, FileAccess.Read);
    }

    public void DeleteFile(string physicalPath)
    {
        if (File.Exists(physicalPath))
        {
            File.Delete(physicalPath);
        }
    }
}
