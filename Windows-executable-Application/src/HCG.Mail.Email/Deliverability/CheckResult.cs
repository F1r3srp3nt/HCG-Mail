namespace HCG.Mail.Email.Deliverability;

/// <summary>
/// How far a check got. Only <see cref="Verified"/> means HCG Mail confirmed something; nothing is ever scored or
/// estimated, and checks that could not run say so as <see cref="NotTested"/>.
/// </summary>
public enum CheckStatus
{
    Verified,
    Warning,
    Failed,
    NotTested,
}

/// <param name="Title">What was checked.</param>
/// <param name="Detail">What was found (record contents, server replies).</param>
/// <param name="Action">What to change and where, when something needs changing outside HCG Mail.</param>
public sealed record CheckResult(string Title, CheckStatus Status, string Detail, string? Action = null)
{
    public string StatusLabel => Label(Status);

    public static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Verified => "Verified",
        CheckStatus.Warning => "Warning",
        CheckStatus.Failed => "Failed",
        _ => "Not tested",
    };
}

public sealed record CheckSection(string Title, string Description, IReadOnlyList<CheckResult> Checks)
{
    /// <summary>The worst status in the section; NotTested only when nothing was tested.</summary>
    public CheckStatus Summary =>
        Checks.Any(c => c.Status == CheckStatus.Failed) ? CheckStatus.Failed
        : Checks.Any(c => c.Status == CheckStatus.Warning) ? CheckStatus.Warning
        : Checks.Any(c => c.Status == CheckStatus.Verified) ? CheckStatus.Verified
        : CheckStatus.NotTested;
}
