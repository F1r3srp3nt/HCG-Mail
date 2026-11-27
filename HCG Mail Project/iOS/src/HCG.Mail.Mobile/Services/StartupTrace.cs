using System.Diagnostics;
using System.Text;
using HCG.Mail.Infrastructure.Logging;

namespace HCG.Mail.Mobile.Services;

/// <summary>The start-up stages, in order. The trace records each one the moment it is reached.</summary>
public enum StartupStage
{
    /// <summary>The process exists (start time from the OS, when available).</summary>
    NativeEntry,
    /// <summary>Managed <c>Main</c> runs: the .NET runtime (Mono AOT on iOS devices) is initialised.</summary>
    RuntimeReady,
    /// <summary>UIKit created the application delegate (UIApplicationMain).</summary>
    AppDelegate,
    /// <summary>Avalonia is being configured (application:didFinishLaunching).</summary>
    AvaloniaInit,
    /// <summary>Avalonia's platform and the application object are set up.</summary>
    AvaloniaReady,
    /// <summary>The app's data folder exists and is writable.</summary>
    LocalStorage,
    /// <summary>The Keychain credential store is created (before the first frame) or probed (after it).</summary>
    Keychain,
    /// <summary>Settings are loaded and the theme is applied.</summary>
    Settings,
    /// <summary>The main view and its view model exist.</summary>
    MainView,
    /// <summary>The main view is attached to the window.</summary>
    MainViewAttached,
    /// <summary>The first frame with the main view has been rendered.</summary>
    FirstFrame,
    /// <summary>Work deferred until after the first frame (temporary files, mailbox start-up).</summary>
    DeferredServices,
}

/// <summary>
/// Start-up breadcrumbs that survive a crash. Each stage is appended to <c>startup.txt</c> in the app's data folder
/// and flushed to disk at once, and mirrored to the console (the device log) as <c>HCG-STARTUP: …</c> lines, so the
/// last line names the last stage that completed. The previous launch's trace is kept as <c>startup-previous.txt</c>.
/// </summary>
/// <remarks>
/// Privacy: only stage names, timings, the app version, the OS version, status codes and exception types, messages
/// and stack traces are written, and every line goes through <see cref="LogRedactor"/>. No password, message, address
/// book entry, token or other credential is ever passed to it. Never throws: diagnostics must not stop the app.
/// </remarks>
public static class StartupTrace
{
    public const string FileName = "startup.txt";
    public const string PreviousFileName = "startup-previous.txt";
    public const string ConsolePrefix = "HCG-STARTUP: ";

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string? _path;
    private static bool _firstFrameRaised;

    /// <summary>The trace file of this launch, or null when no folder could be written.</summary>
    public static string? FilePath { get { lock (Gate) return _path; } }

    /// <summary>The last stage this launch reached.</summary>
    public static StartupStage? LastStage { get; private set; }

    /// <summary>
    /// When the previous launch stopped before its first frame: the last stage it recorded (or "none"). Null when it
    /// reached its first frame, or there was no previous launch.
    /// </summary>
    public static string? PreviousLaunchStoppedAt { get; private set; }

    /// <summary>Raised once, on the UI thread, after <see cref="StartupStage.FirstFrame"/>: for non-essential checks.</summary>
    public static event Action? FirstFrameRendered;

    /// <summary>
    /// Starts the trace in the first writable folder of <paramref name="directories"/>: keeps the last launch's trace
    /// as startup-previous.txt and writes <paramref name="header"/> (app and OS version; never user data).
    /// </summary>
    public static string? Begin(IEnumerable<string> directories, string header)
    {
        lock (Gate)
        {
            foreach (var dir in directories)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, FileName);
                    var previous = Path.Combine(dir, PreviousFileName);
                    if (File.Exists(path))
                    {
                        PreviousLaunchStoppedAt = StoppedBeforeFirstFrame(File.ReadAllLines(path));
                        File.Copy(path, previous, overwrite: true);
                    }
                    Append(path, $"HCG Mail start-up trace · {Redact(header)} · {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z", truncate: true);
                    _path = path;
                    return path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    // Try the next folder.
                }
            }
            return null;
        }
    }

    /// <summary>Records that <paramref name="stage"/> was reached. <paramref name="detail"/> must not contain user data.</summary>
    public static void Mark(StartupStage stage, string? detail = null)
    {
        LastStage = stage;
        Write($"{Name(stage)}{(string.IsNullOrEmpty(detail) ? "" : " · " + detail)}");
        if (stage == StartupStage.FirstFrame && !_firstFrameRaised)
        {
            _firstFrameRaised = true;
            try { FirstFrameRendered?.Invoke(); }
            catch (Exception ex) { Failure("first-frame checks", ex); }
        }
    }

    /// <summary>Records an exception (type, HResult, redacted message and stack trace, inner exceptions).</summary>
    public static void Failure(string where, Exception exception)
    {
        var sb = new StringBuilder($"FAILED in {where} after {(LastStage is { } s ? Name(s) : "start")}");
        var depth = 0;
        for (var e = exception; e is not null && depth < 5; e = e.InnerException, depth++)
        {
            sb.Append(depth == 0 ? "\n  " : "\n  caused by ")
              .Append(e.GetType().FullName).Append($" (0x{e.HResult:X8}): ").Append(e.Message);
            if (e.StackTrace is { Length: > 0 } stack)
                foreach (var line in stack.Split('\n').Take(40))
                    sb.Append("\n    ").Append(line.TrimEnd());
        }
        Write(sb.ToString());
    }

    /// <summary>The stage's name as written to the trace ("first-frame").</summary>
    public static string Name(StartupStage stage) => stage switch
    {
        StartupStage.NativeEntry => "native-entry",
        StartupStage.RuntimeReady => "runtime-ready",
        StartupStage.AppDelegate => "app-delegate",
        StartupStage.AvaloniaInit => "avalonia-init",
        StartupStage.AvaloniaReady => "avalonia-ready",
        StartupStage.LocalStorage => "local-storage",
        StartupStage.Keychain => "keychain",
        StartupStage.Settings => "settings",
        StartupStage.MainView => "main-view",
        StartupStage.MainViewAttached => "main-view-attached",
        StartupStage.FirstFrame => "first-frame",
        StartupStage.DeferredServices => "deferred-services",
        _ => stage.ToString(),
    };

    /// <summary>Null if the trace reached its first frame; otherwise the last stage it names ("none" if no stage).</summary>
    internal static string? StoppedBeforeFirstFrame(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return null;
        string? last = null;
        foreach (var line in lines)
        {
            var stage = StageOf(line);
            if (stage == Name(StartupStage.FirstFrame))
                return null;
            if (stage is not null)
                last = stage;
        }
        return last ?? "none";
    }

    private static string? StageOf(string line)
    {
        // "+    123 ms  first-frame · detail"
        var i = line.IndexOf(" ms  ", StringComparison.Ordinal);
        if (!line.StartsWith('+') || i < 0)
            return null;
        var rest = line[(i + 5)..];
        var end = rest.IndexOf(' ');
        var name = end < 0 ? rest : rest[..end];
        return Enum.GetValues<StartupStage>().Select(Name).Contains(name) ? name : null;
    }

    private static void Write(string text)
    {
        var line = $"+{Clock.ElapsedMilliseconds,7} ms  {Redact(text)}";
        try { Console.WriteLine(ConsolePrefix + line.Replace("\n", "\n" + ConsolePrefix)); }
        catch (Exception) { /* no console */ }
        lock (Gate)
        {
            if (_path is null)
                return;
            try { Append(_path, line, truncate: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* keep running */ }
        }
    }

    private static void Append(string path, string line, bool truncate)
    {
        using var stream = new FileStream(path, truncate ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true); // the line must survive the process being killed right after it
    }

    private static string Redact(string text)
    {
        try { return LogRedactor.Redact(text); }
        catch (Exception) { return "[unreadable]"; }
    }

    /// <summary>Tests only: forget this process's trace.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _path = null;
            _firstFrameRaised = false;
            LastStage = null;
            PreviousLaunchStoppedAt = null;
            FirstFrameRendered = null;
        }
    }
}
