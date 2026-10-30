using System.Globalization;
using HCG.Mail.Core.Addresses;

namespace HCG.Mail.Core.Accounts;

public enum AccountField
{
    DisplayName,
    EmailAddress,
    Username,
    Password,
    ImapHost,
    ImapPort,
    SmtpHost,
    SmtpPort,
}

public sealed record ValidationError(AccountField Field, string Message);

/// <summary>Raw values typed into the account form, before they become a <see cref="MailAccount"/>.</summary>
public sealed record AccountDraft
{
    public string DisplayName { get; init; } = "";
    public string EmailAddress { get; init; } = "";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string ImapHost { get; init; } = "";
    public string ImapPort { get; init; } = "";
    public MailSecurityMode ImapSecurity { get; init; } = MailSecurityMode.SslTls;
    public string SmtpHost { get; init; } = "";
    public string SmtpPort { get; init; } = "";
    public MailSecurityMode SmtpSecurity { get; init; } = MailSecurityMode.SslTls;
}

public static class AccountValidator
{
    public const int MaxHostLength = 253;

    /// <param name="requirePassword">False when editing an account whose password is already stored.</param>
    public static IReadOnlyList<ValidationError> Validate(AccountDraft draft, bool requirePassword = true)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(draft.EmailAddress))
            errors.Add(new(AccountField.EmailAddress, "Enter your email address."));
        else if (!EmailAddressValidator.IsValid(draft.EmailAddress.Trim()))
            errors.Add(new(AccountField.EmailAddress, "This does not look like a valid email address."));

        if (draft.DisplayName.Length > 200)
            errors.Add(new(AccountField.DisplayName, "The display name is too long."));
        if (draft.DisplayName.IndexOfAny(['\r', '\n']) >= 0)
            errors.Add(new(AccountField.DisplayName, "The display name cannot contain line breaks."));

        if (string.IsNullOrWhiteSpace(draft.Username))
            errors.Add(new(AccountField.Username, "Enter the username for your mailbox (usually the full email address)."));
        else if (draft.Username.IndexOfAny(['\r', '\n', '\0']) >= 0)
            errors.Add(new(AccountField.Username, "The username contains invalid characters."));

        if (requirePassword && string.IsNullOrEmpty(draft.Password))
            errors.Add(new(AccountField.Password, "Enter your password."));
        else if (draft.Password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            errors.Add(new(AccountField.Password, "The password contains invalid characters."));

        ValidateHost(draft.ImapHost, AccountField.ImapHost, "incoming (IMAP)", errors);
        ValidatePort(draft.ImapPort, AccountField.ImapPort, errors);
        ValidateHost(draft.SmtpHost, AccountField.SmtpHost, "outgoing (SMTP)", errors);
        ValidatePort(draft.SmtpPort, AccountField.SmtpPort, errors);

        return errors;
    }

    public static MailAccount ToAccount(AccountDraft draft, string? existingId = null, string? presetId = null)
    {
        var errors = Validate(draft, requirePassword: false);
        if (errors.Count > 0)
            throw new ArgumentException($"Account is invalid: {errors[0].Message}", nameof(draft));

        var email = draft.EmailAddress.Trim();
        return new MailAccount
        {
            Id = existingId ?? MailAccount.NewId(),
            DisplayName = draft.DisplayName.Trim(),
            EmailAddress = email,
            Username = draft.Username.Trim(),
            Imap = new ServerEndpoint(NormalizeHost(draft.ImapHost), int.Parse(draft.ImapPort.Trim(), CultureInfo.InvariantCulture), draft.ImapSecurity),
            Smtp = new ServerEndpoint(NormalizeHost(draft.SmtpHost), int.Parse(draft.SmtpPort.Trim(), CultureInfo.InvariantCulture), draft.SmtpSecurity),
            PresetId = presetId,
        };
    }

    public static AccountDraft FromAccount(MailAccount account) => new()
    {
        DisplayName = account.DisplayName,
        EmailAddress = account.EmailAddress,
        Username = account.Username,
        ImapHost = account.Imap.Host,
        ImapPort = account.Imap.Port.ToString(CultureInfo.InvariantCulture),
        ImapSecurity = account.Imap.Security,
        SmtpHost = account.Smtp.Host,
        SmtpPort = account.Smtp.Port.ToString(CultureInfo.InvariantCulture),
        SmtpSecurity = account.Smtp.Security,
    };

    public static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    public static bool IsValidHostname(string host)
    {
        host = NormalizeHost(host);
        if (host.Length == 0 || host.Length > MaxHostLength)
            return false;

        // Literal IP addresses are allowed (e.g. a server reached via VPN), but certificate
        // validation will still apply to them.
        if (System.Net.IPAddress.TryParse(host, out _))
            return true;

        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
                return false;
            if (label[0] == '-' || label[^1] == '-')
                return false;
            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                    return false;
            }
        }
        return true;
    }

    private static void ValidateHost(string host, AccountField field, string description, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(host))
            errors.Add(new(field, $"Enter the {description} server name."));
        else if (host.Contains("://", StringComparison.Ordinal))
            errors.Add(new(field, "Enter only the server name, without http:// or imap://."));
        else if (!IsValidHostname(host))
            errors.Add(new(field, $"'{host.Trim()}' is not a valid server name."));
    }

    private static void ValidatePort(string port, AccountField field, List<ValidationError> errors)
    {
        if (!int.TryParse(port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > 65535)
            errors.Add(new(field, "Enter a port number between 1 and 65535."));
    }
}
