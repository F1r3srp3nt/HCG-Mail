namespace HCG.Mail.Infrastructure;

/// <summary>Per-user locations for HCG Mail's configuration and logs. Message content is never written here.</summary>
public sealed class AppPaths
{
    public const string DataDirectoryOverrideVariable = "HCG_MAIL_DATA_DIR";

    public AppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string AccountsFile => Path.Combine(DataDirectory, "accounts.json");

    public static AppPaths ForCurrentUser()
    {
        var overrideDir = Environment.GetEnvironmentVariable(DataDirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return new AppPaths(Path.GetFullPath(overrideDir));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dir;
        if (OperatingSystem.IsWindows())
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HCG", "HCG Mail");
        else if (OperatingSystem.IsMacOS())
            dir = Path.Combine(home, "Library", "Application Support", "HCG Mail");
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            dir = Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg, "hcg-mail");
        }
        return new AppPaths(dir);
    }

    public void EnsureCreated()
    {
        CreatePrivateDirectory(DataDirectory);
        CreatePrivateDirectory(LogDirectory);
    }

    public static string DefaultDownloadDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        return Directory.Exists(downloads) ? downloads : home;
    }

    /// <summary>Creates a directory readable only by the current user on Unix-like systems (Windows profile folders are already per-user).</summary>
    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
