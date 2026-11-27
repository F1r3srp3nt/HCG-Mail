namespace HCG.Mail.Core.Network;

/// <summary>
/// Where the Network Doctor's general connectivity checks go: a plain-HTTP Wi-Fi sign-in check, an HTTPS internet and
/// certificate check, a public name lookup and, optionally, a large mail provider's ports for comparison. Providers are
/// interchangeable; with <see cref="ConnectivityProviders.Off"/> nothing beyond the user's own mail servers is contacted.
/// </summary>
public sealed record ConnectivityProvider(
    string Id,
    string DisplayName,
    string Operator,
    bool IsThirdParty,
    Uri? CaptivePortalCheck = null,
    int CaptiveExpectedStatus = 204,
    string? CaptiveExpectedBody = null,
    Uri? InternetCheck = null,
    string? PublicDnsName = null,
    string? ReferenceIncomingHost = null,
    int ReferenceIncomingPort = 993,
    string? ReferenceOutgoingHost = null,
    IReadOnlyList<int>? ReferenceOutgoingPorts = null)
{
    public override string ToString() => DisplayName;

    public bool ChecksConnectivity => CaptivePortalCheck is not null || InternetCheck is not null || PublicDnsName is not null;

    public bool ComparesMailPorts => ReferenceIncomingHost is not null || ReferenceOutgoingHost is not null;

    /// <summary>The comparison port for sending: the account's own port when this provider offers it, else its first.</summary>
    public int ReferenceOutgoingPortFor(int accountPort) =>
        (ReferenceOutgoingPorts ?? []).Contains(accountPort) ? accountPort : (ReferenceOutgoingPorts ?? [587]).FirstOrDefault(587);

    /// <summary>Whether the plain-HTTP check was answered by the expected server (anything else is a sign-in page).</summary>
    public bool IsExpectedCaptiveAnswer(int status, string body) =>
        status == CaptiveExpectedStatus && (CaptiveExpectedBody is null ? string.IsNullOrEmpty(body) : body.Contains(CaptiveExpectedBody, StringComparison.Ordinal));

    public IReadOnlyList<string> ContactedHosts =>
        new[] { CaptivePortalCheck?.Host, InternetCheck?.Host, PublicDnsName, ReferenceIncomingHost, ReferenceOutgoingHost }
            .Where(h => !string.IsNullOrEmpty(h)).Select(h => h!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public string ContactedHostsText => !ChecksConnectivity && !ComparesMailPorts
        ? "Connectivity checks are off: the Network Doctor contacts only your own mail servers. Results about Wi-Fi sign-in pages, internet access and whether this network blocks mail in general are then not available."
        : $"While it runs, the Network Doctor contacts {string.Join(", ", ContactedHosts)} ({Operator}) to check Wi-Fi sign-in pages, internet access" +
          (ComparesMailPorts ? " and, by opening and closing a connection without sending anything, whether this network allows mail ports at all" : "") +
          ". Your password goes only to your own mail servers, over a verified encrypted connection. You can choose another provider, or turn these checks off, in Settings › Privacy & security.";
}

public static class ConnectivityProviders
{
    /// <summary>Google's checks, as used by Android and Chrome; Gmail's ports for comparison.</summary>
    public static readonly ConnectivityProvider Google = new(
        "google", "Google (as used by Android and Chrome)", "operated by Google", IsThirdParty: true,
        CaptivePortalCheck: new Uri("http://connectivitycheck.gstatic.com/generate_204"),
        InternetCheck: new Uri("https://www.gstatic.com/generate_204"),
        PublicDnsName: "www.gstatic.com",
        ReferenceIncomingHost: "imap.gmail.com", ReferenceOutgoingHost: "smtp.gmail.com", ReferenceOutgoingPorts: [465, 587]);

    /// <summary>Apple's checks, as used by macOS and iOS; iCloud Mail's ports for comparison.</summary>
    public static readonly ConnectivityProvider Apple = new(
        "apple", "Apple (as used by macOS and iOS)", "operated by Apple", IsThirdParty: true,
        CaptivePortalCheck: new Uri("http://captive.apple.com/hotspot-detect.html"), CaptiveExpectedStatus: 200, CaptiveExpectedBody: "Success",
        InternetCheck: new Uri("https://www.apple.com/library/test/success.html"),
        PublicDnsName: "captive.apple.com",
        ReferenceIncomingHost: "imap.mail.me.com", ReferenceOutgoingHost: "smtp.mail.me.com", ReferenceOutgoingPorts: [587]);

    /// <summary>Microsoft's checks, as used by Windows; Microsoft 365's ports for comparison.</summary>
    public static readonly ConnectivityProvider Microsoft = new(
        "microsoft", "Microsoft (as used by Windows)", "operated by Microsoft", IsThirdParty: true,
        CaptivePortalCheck: new Uri("http://www.msftconnecttest.com/connecttest.txt"), CaptiveExpectedStatus: 200, CaptiveExpectedBody: "Microsoft Connect Test",
        InternetCheck: new Uri("https://www.microsoft.com/"),
        PublicDnsName: "www.msftconnecttest.com",
        ReferenceIncomingHost: "outlook.office365.com", ReferenceOutgoingHost: "smtp.office365.com", ReferenceOutgoingPorts: [587]);

    /// <summary>No third-party checks: only the account's own servers are diagnosed.</summary>
    public static readonly ConnectivityProvider Off = new("off", "Off — check only my own mail servers", "nobody else", IsThirdParty: false);

    public const string HcgId = "hcg";

    /// <summary>
    /// A future HCG-operated endpoint. Offered only when a build or the environment names one
    /// (<c>HcgConnectivityCheckUrl</c> / <c>HCG_CONNECTIVITY_CHECK_URL</c>); HCG Mail never requires it. Contract: GET
    /// <c>{base}/generate_204</c> answers 204 with an empty body over both HTTP and HTTPS.
    /// </summary>
    public static ConnectivityProvider? Hcg(Uri? baseUrl) =>
        baseUrl is null || baseUrl.Scheme != Uri.UriSchemeHttps ? null : new(
            HcgId, "HCG (operated by Hakmi Capital Group)", "operated by HCG", IsThirdParty: false,
            CaptivePortalCheck: new UriBuilder(baseUrl) { Scheme = Uri.UriSchemeHttp, Port = -1, Path = baseUrl.AbsolutePath.TrimEnd('/') + "/generate_204" }.Uri,
            InternetCheck: new Uri(baseUrl, baseUrl.AbsolutePath.TrimEnd('/') + "/generate_204"),
            PublicDnsName: baseUrl.Host);

    public static IReadOnlyList<ConnectivityProvider> Available(Uri? hcgEndpoint) =>
        Hcg(hcgEndpoint) is { } hcg ? [Google, Apple, Microsoft, hcg, Off] : [Google, Apple, Microsoft, Off];

    /// <summary>The chosen provider, or Google when the choice is unknown (for example an HCG endpoint this build does not have).</summary>
    public static ConnectivityProvider Resolve(string? id, Uri? hcgEndpoint) =>
        Available(hcgEndpoint).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Google;
}

/// <summary>Where a future HCG connectivity endpoint comes from: the build (assembly metadata) or the environment. Optional.</summary>
public static class HcgConnectivityEndpoint
{
    public const string MetadataKey = "HcgConnectivityCheckUrl";
    public const string EnvironmentVariable = "HCG_CONNECTIVITY_CHECK_URL";

    public static Uri? Resolve(System.Reflection.Assembly assembly)
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
            configured = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value;
        return Uri.TryCreate(configured?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }
}

/// <summary>The provider in use now (follows the user's setting).</summary>
public sealed class ConnectivityProviderSelection(Func<ConnectivityProvider> current, IReadOnlyList<ConnectivityProvider>? available = null)
{
    public ConnectivityProvider Current => current();

    public IReadOnlyList<ConnectivityProvider> Available { get; } = available ?? ConnectivityProviders.Available(null);
}
