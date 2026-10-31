using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Storage;

/// <summary>Small JSON persistence helper with atomic replace and recovery from corrupted files.</summary>
internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? Read<T>(string path, ILogger logger) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            var backup = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            logger.LogWarning("Could not read {File}; moved it to {Backup} and using defaults ({Error})", Path.GetFileName(path), Path.GetFileName(backup), ex.GetType().Name);
            try
            {
                File.Move(path, backup);
            }
            catch (IOException)
            {
            }
            return null;
        }
    }

    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        AppPaths.CreatePrivateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
