namespace HCG.Mail.App.Services;

/// <summary>
/// How the interface presents mailbox sign-in providers (Microsoft, Google) that are not configured in this build.
/// The desktop app offers them and explains in their dialog what is missing. A store release (the iPhone/iPad app)
/// hides them until the provider is configured, so nothing unavailable is advertised; the implementation stays in place.
/// </summary>
public sealed record SignInPresentation(bool OfferUnconfiguredProviders)
{
    public static SignInPresentation Default { get; } = new(OfferUnconfiguredProviders: true);
}
