using System.Globalization;
using HCG.Mail.Email;
using HCG.Mail.Email.Diagnostics;
using HCG.Mail.Infrastructure;

namespace HCG.Mail.App;

/// <summary>
/// <c>HCGMail --diagnose-tls host[:port]</c>: read-only TLS/certificate diagnosis for a mail server.
/// No credentials are read or sent. Prints the report and saves it to the settings folder.
/// Exit code 0 when HCG Mail's own strict connection check passes.
/// </summary>
internal static class TlsDiagnosisCommand
{
    public const string Argument = "--diagnose-tls";

    public static int? TryRun(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return null;
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"Usage: HCGMail {Argument} host[:port]   (default port 993; use 465 for SMTP)");
            return 2;
        }

        var target = args[index + 1];
        var host = target;
        var port = 993;
        var colon = target.LastIndexOf(':');
        if (colon > 0 && int.TryParse(target[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p))
        {
            host = target[..colon];
            port = p;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var ok = new TlsDiagnostics(new MailClientFactory(), http).RunAsync(host, port, writer).GetAwaiter().GetResult();
        var report = writer.ToString();
        Console.Write(report);
        try
        {
            var paths = AppPaths.ForCurrentUser();
            paths.EnsureCreated();
            var file = Path.Combine(paths.DataDirectory, $"tls-diagnosis-{host}-{port}.txt");
            File.WriteAllText(file, report);
            Console.WriteLine($"Report saved to: {file}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return ok ? 0 : 1;
    }
}
