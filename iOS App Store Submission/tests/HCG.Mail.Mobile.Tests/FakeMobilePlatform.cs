using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Security;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Mobile.Platform;

namespace HCG.Mail.Mobile.Tests;

/// <summary>An in-memory platform: temporary folders, an in-memory vault, and recorded share/open requests.</summary>
public sealed class FakeMobilePlatform : IMobilePlatform, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hcg-mobile-tests", Guid.NewGuid().ToString("N"));
    private double _textScale = 1;
    private bool _reducedMotion;

    public FakeMobilePlatform()
    {
        Paths = new AppPaths(Path.Combine(_root, "Library", "Application Support", "HCG Mail"));
        TemporaryFilesDirectory = Path.Combine(_root, "tmp");
    }

    public string Description => "Test device";
    public AppPaths Paths { get; }
    public string TemporaryFilesDirectory { get; }
    public ICredentialVault CredentialVault { get; } = new SessionCredentialVault();
    public IMicrosoftAuth MicrosoftAuth { get; set; } = new UnconfiguredMicrosoftAuth("Microsoft sign-in is not set up in this test build.");
    public bool IsTablet { get; set; }
    public List<Uri> OpenedUrls { get; } = [];
    public List<string> SharedFiles { get; } = [];

    public double TextScale
    {
        get => _textScale;
        set
        {
            _textScale = value;
            AccessibilitySettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool PrefersReducedMotion
    {
        get => _reducedMotion;
        set
        {
            _reducedMotion = value;
            AccessibilitySettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? AccessibilitySettingsChanged;

    public void OpenUrl(Uri uri) => OpenedUrls.Add(uri);

    public Task ShareFileAsync(string path)
    {
        SharedFiles.Add(path);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }
}
