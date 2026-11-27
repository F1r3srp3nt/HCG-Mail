using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Storage;

/// <summary>
/// Small JSON persistence helper with atomic replace and recovery from corrupted files. Types come from
/// <see cref="StorageJsonContext"/> (source-generated, no reflection).
/// </summary>
internal static class JsonFile
{
    public static T? Read<T>(string path, JsonTypeInfo<T> typeInfo, ILogger logger) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo);
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

    public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var directory = Path.GetDirectoryName(path)!;
        AppPaths.CreatePrivateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, typeInfo);
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
