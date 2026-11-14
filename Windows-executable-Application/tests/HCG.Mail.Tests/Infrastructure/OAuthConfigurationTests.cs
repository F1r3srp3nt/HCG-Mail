using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HCG.Mail.Tests.Infrastructure;

/// <summary>
/// The OAuth client registrations must reach every build the same way (oauth-clients.props): environment variables
/// (CI, publish scripts, HCG Executable Maker), -p: properties, the untracked local file — and the iOS projects,
/// which import the same Directory.Build.props. Evaluates the real project files with `dotnet msbuild`.
/// </summary>
public sealed class OAuthConfigurationTests
{
    private const string MicrosoftId = "11111111-2222-3333-4444-555555555555";
    private const string GoogleId = "123456789012-abcdefghijklmnopqrstuvwxyz012345.apps.googleusercontent.com";

    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "oauth-clients.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("oauth-clients.props not found");
    }

    private static string DesktopProject => Path.Combine(AppRoot(), "src", "HCG.Mail.App", "HCG.Mail.App.csproj");

    private static string Msbuild(string project, string query, Dictionary<string, string?>? env = null, params string[] properties)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add(query);
        foreach (var p in properties)
            start.ArgumentList.Add("-p:" + p);
        // A clean environment for the values under test, whatever the machine running the tests has set.
        foreach (var name in new[] { "HCG_MICROSOFT_CLIENT_ID", "HCG_GOOGLE_CLIENT_ID", "HCG_GOOGLE_CLIENT_SECRET" })
            start.Environment[name] = env is not null && env.TryGetValue(name, out var v) ? v : null;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(120_000);
        Assert.True(process.ExitCode == 0, output + process.StandardError.ReadToEnd());
        return output;
    }

    private static Dictionary<string, string> Metadata(Dictionary<string, string?>? env = null, params string[] properties)
    {
        using var json = JsonDocument.Parse(Msbuild(DesktopProject, "-getItem:AssemblyMetadata", env, properties));
        return json.RootElement.GetProperty("Items").GetProperty("AssemblyMetadata").EnumerateArray()
            .ToDictionary(i => i.GetProperty("Identity").GetString()!, i => i.GetProperty("Value").GetString() ?? "");
    }

    private static bool LocalFileExists => File.Exists(Path.Combine(AppRoot(), "oauth-clients.local.props"));

    [Fact]
    public void Environment_variables_reach_the_desktop_build()
    {
        if (LocalFileExists)
            return; // a developer's own local file would take part; the CI checkout has none
        var meta = Metadata(new() { ["HCG_MICROSOFT_CLIENT_ID"] = MicrosoftId, ["HCG_GOOGLE_CLIENT_ID"] = GoogleId, ["HCG_GOOGLE_CLIENT_SECRET"] = "test-secret" });
        Assert.Equal(MicrosoftId, meta["HcgMicrosoftClientId"]);
        Assert.Equal(GoogleId, meta["HcgGoogleClientId"]);
        Assert.Equal("test-secret", meta["HcgGoogleClientSecret"]);
    }

    [Fact]
    public void Command_line_values_win_over_environment_variables()
    {
        var meta = Metadata(new() { ["HCG_MICROSOFT_CLIENT_ID"] = "00000000-0000-0000-0000-00000000000a" }, "HcgMicrosoftClientId=" + MicrosoftId);
        Assert.Equal(MicrosoftId, meta["HcgMicrosoftClientId"]);
    }

    [Fact]
    public void Without_configuration_the_values_stay_empty_and_nothing_is_invented()
    {
        if (LocalFileExists)
            return;
        var meta = Metadata();
        var committed = File.ReadAllText(Path.Combine(AppRoot(), "oauth-clients.props"));
        // Empty, or exactly what HCG committed after registering — never a made-up placeholder.
        Assert.True(meta["HcgMicrosoftClientId"] == "" || committed.Contains(meta["HcgMicrosoftClientId"], StringComparison.Ordinal));
        Assert.True(meta["HcgGoogleClientId"] == "" || committed.Contains(meta["HcgGoogleClientId"], StringComparison.Ordinal));
        Assert.Equal("", meta["HcgGoogleClientSecret"]); // never comes from the committed file
    }

    [Fact]
    public void The_untracked_local_file_is_used_and_environment_variables_win_over_it()
    {
        if (LocalFileExists)
            return;
        var path = Path.Combine(AppRoot(), "oauth-clients.local.props");
        File.WriteAllText(path, $"""
            <Project><PropertyGroup>
              <HcgGoogleClientId Condition="'$(HcgGoogleClientId)' == ''">{GoogleId}</HcgGoogleClientId>
              <HcgGoogleClientSecret Condition="'$(HcgGoogleClientSecret)' == ''">local-secret</HcgGoogleClientSecret>
            </PropertyGroup></Project>
            """);
        try
        {
            var meta = Metadata();
            Assert.Equal(GoogleId, meta["HcgGoogleClientId"]);
            Assert.Equal("local-secret", meta["HcgGoogleClientSecret"]);
            Assert.Equal("env-secret", Metadata(new() { ["HCG_GOOGLE_CLIENT_SECRET"] = "env-secret" })["HcgGoogleClientSecret"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_iOS_projects_get_the_same_values()
    {
        var mobile = Path.Combine(AppRoot(), "..", "iOS App Store Submission", "src", "HCG.Mail.Mobile", "HCG.Mail.Mobile.csproj");
        if (!File.Exists(mobile))
            return;
        var value = Msbuild(mobile, "-getProperty:HcgMicrosoftClientId", new() { ["HCG_MICROSOFT_CLIENT_ID"] = MicrosoftId }).Trim();
        Assert.Equal(MicrosoftId, value);
    }

    [Fact]
    public void Git_ignores_the_local_file_and_the_committed_file_holds_no_secret()
    {
        var root = Path.GetFullPath(Path.Combine(AppRoot(), ".."));
        Assert.Contains("oauth-clients.local.props", File.ReadAllText(Path.Combine(root, ".gitignore")));
        var committed = File.ReadAllText(Path.Combine(AppRoot(), "oauth-clients.props"));
        Assert.DoesNotContain("GOCSPX", committed, StringComparison.Ordinal); // Google client secrets start with GOCSPX-
        Assert.DoesNotMatch(new Regex(@"<HcgGoogleClientSecret[^>]*>[^<$]"), committed); // only $(…) environment fallbacks
        foreach (Match m in Regex.Matches(committed, @"<HcgMicrosoftClientId Condition=""[^""]*"">([^<$]+)<"))
            Assert.Matches("^[0-9a-fA-F-]{36}$", m.Groups[1].Value);
    }
}
