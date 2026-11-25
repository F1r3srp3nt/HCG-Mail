using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Foundation;
using HCG.Mail.Infrastructure;
using HCG.Mail.iOS.Platform;
using HCG.Mail.Mobile.Services;
using UIKit;

namespace HCG.Mail.iOS;

public static class Application
{
    private static void Main(string[] args)
    {
        BeginStartupTrace();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                StartupTrace.Failure("an unhandled exception (the app closes)", ex);
        };
        ObjCRuntime.Runtime.MarshalManagedException += (_, e) =>
            StartupTrace.Failure($"managed code called from iOS ({e.ExceptionMode})", e.Exception);
        try
        {
            UIApplication.Main(args, null, typeof(AppDelegate));
        }
        catch (Exception ex)
        {
            StartupTrace.Failure("UIApplicationMain", ex);
            throw;
        }
    }

    /// <summary>
    /// Starts the start-up trace (StartupTrace) in the app's data folder, or its temporary folder if that cannot be
    /// written. The header names the build, the iOS version and where the app runs from (bundle, home and temporary
    /// folders, which show a rootless install and whether a sandbox container exists); no user data.
    /// </summary>
    private static void BeginStartupTrace()
    {
        try
        {
            var name = IosPlatform.AppName;
            var dirs = new List<string>();
            try { dirs.Add(Path.Combine(AppPaths.ForCurrentUser(name).DataDirectory, "startup")); }
            catch (Exception) { /* fall back to the temporary folder */ }
            dirs.Add(Path.Combine(Path.GetTempPath(), name + " startup"));
            var bundle = NSBundle.MainBundle;
            var version = $"{bundle.ObjectForInfoDictionary("CFBundleShortVersionString")} ({bundle.ObjectForInfoDictionary("CFBundleVersion")})";
            StartupTrace.Begin(dirs,
                $"{name} {version}, {bundle.BundleIdentifier} · {NSProcessInfo.ProcessInfo.OperatingSystemVersionString} · "
                + $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}, dynamic code {(RuntimeFeature.IsDynamicCodeSupported ? "supported (interpreter)" : "not supported (AOT only)")} · "
                + $"bundle {bundle.BundlePath} · home {NSFileManager.HomeDirectory} · temp {Path.GetTempPath()}");
            // iOS gives no process start time to .NET; the trace's own clock starts with the runtime, so the time
            // the runtime itself took is the gap between launch and this line in the device log.
            StartupTrace.Mark(StartupStage.NativeEntry, "process running");
            StartupTrace.Mark(StartupStage.RuntimeReady, "managed Main");
        }
        catch (Exception ex)
        {
            Console.WriteLine(StartupTrace.ConsolePrefix + "could not start the trace: " + ex.GetType().Name);
        }
    }
}
