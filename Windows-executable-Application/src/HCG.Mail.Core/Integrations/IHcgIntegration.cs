namespace HCG.Mail.Core.Integrations;

/// <summary>
/// Extension point for future, separately approved Hakmi Capital Group integrations
/// (for example HCG Connect or HCG ERP). HCG Mail ships with no integrations registered and
/// works fully without them. Implementations must not receive account credentials or message
/// content unless the user explicitly invokes them, and must be registered by the host
/// application — never discovered or downloaded at runtime.
/// </summary>
public interface IHcgIntegration
{
    string Id { get; }

    string DisplayName { get; }

    bool IsEnabled { get; }
}
