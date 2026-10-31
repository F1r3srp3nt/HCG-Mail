using System.Globalization;
using System.Text;

namespace HCG.Mail.Core.Mail;

/// <summary>
/// Turns sender-controlled attachment names into names that are safe to write to disk on
/// Windows, macOS and Linux: no path traversal, no reserved device names, no control or
/// bidirectional-override characters (used to disguise "exe" as "pdf"), and a bounded length.
/// </summary>
public static class AttachmentFileNames
{
    public const string Fallback = "attachment";
    private const int MaxLength = 150;

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Extensions that execute code or are commonly abused for that when opened.
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".msi", ".msp", ".msc", ".cpl", ".dll",
        ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".ps1", ".psm1", ".hta", ".lnk", ".url",
        ".reg", ".jar", ".app", ".command", ".sh", ".iso", ".img", ".vhd", ".vhdx", ".appx",
        ".msix", ".application", ".gadget", ".scf", ".chm", ".docm", ".xlsm", ".pptm", ".xll",
    };

    public static string Sanitize(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Fallback;

        // Keep only the last path segment, whichever separator the sender used.
        var name = fileName.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];

        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(c) || IsBidiOrInvisibleFormat(c))
                continue;
            sb.Append(c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c);
        }
        name = sb.ToString().Trim();

        // Windows silently strips trailing dots and spaces, which can change the extension.
        name = name.TrimEnd('.', ' ');
        while (name.StartsWith('.'))
            name = name[1..];

        if (name.Length == 0)
            return Fallback;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        if (ReservedWindowsNames.Contains(stem.Split('.')[0]))
            name = "_" + name;

        if (name.Length > MaxLength)
        {
            if (ext.Length > 20)
                ext = "";
            stem = Path.GetFileNameWithoutExtension(name);
            var info = new StringInfo(stem);
            var keep = Math.Max(1, MaxLength - ext.Length);
            stem = info.LengthInTextElements > keep ? info.SubstringByTextElements(0, keep) : stem;
            name = stem + ext;
        }

        return name;
    }

    public static bool IsPotentiallyDangerous(string fileName) =>
        DangerousExtensions.Contains(Path.GetExtension(Sanitize(fileName)));

    /// <summary>Returns a path inside <paramref name="directory"/> that does not exist yet, e.g. "report (2).pdf".</summary>
    public static string GetUniquePath(string directory, string safeFileName)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var candidate = Path.GetFullPath(Path.Combine(fullDirectory, safeFileName));
        EnsureInside(fullDirectory, candidate);
        if (!File.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(safeFileName);
        var ext = Path.GetExtension(safeFileName);
        for (var i = 2; i < 10_000; i++)
        {
            candidate = Path.Combine(fullDirectory, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
        throw new IOException("Could not find a free file name for the attachment.");
    }

    public static void EnsureInside(string directory, string path)
    {
        var dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(dir, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The attachment path resolves outside the download folder.");
    }

    private static bool IsBidiOrInvisibleFormat(char c) =>
        c is '​' or '‌' or '‍' or '‎' or '‏' or '﻿' or '⁠'
        || (c >= '‪' && c <= '‮')
        || (c >= '⁦' && c <= '⁩');
}
