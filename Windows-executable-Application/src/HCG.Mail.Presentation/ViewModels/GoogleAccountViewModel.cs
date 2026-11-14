using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Services;
using HCG.Mail.Email.Gmail;

namespace HCG.Mail.App.ViewModels;

/// <summary>
/// Adds or reconnects a Google (Gmail / Google Workspace) account: sign-in happens on Google's page in the system
/// browser, then HCG Mail checks that Gmail is reachable before saving anything.
/// </summary>
public sealed partial class GoogleAccountViewModel : ObservableObject
{
    public const string PrivacyText =
        "You sign in on Google's own page in your web browser. HCG Mail never sees your Google password and stores no password; " +
        "Google gives HCG Mail a sign-in token that is kept in the same protected credential storage as account passwords (for example the macOS Keychain or Windows Credential Manager).";

    public const string RoleText =
        "Google access covers this mailbox only. It gives no roles or permissions in HCG Mail, HCG Connect or any other HCG system.";

    public const string SupportText =
        "Works with personal Gmail accounts and Google Workspace accounts that have Gmail. Gmail labels appear as folders; " +
        "\u201cAll Mail\u201d is the archive. Messages can be moved to Trash; Gmail deletes them from Trash after 30 days.";

    public const string TestingText =
        "While HCG Mail's Google registration is in testing, only accounts HCG has added as test users can sign in, and Google ends the " +
        "sign-in after 7 days (reconnect then). Some Google Workspace organisations only allow apps their administrator has approved.";

    private readonly IGoogleAuth _auth;
    private readonly IMailSessionFactory _sessions;
    private readonly Func<MailAccount, Task> _onConnected;
    private readonly Func<string, bool> _isAccountKeyInUse;
    private readonly Func<MailAccount, Task<bool>>? _onRemove;
    private readonly MailAccount? _existing;
    private CancellationTokenSource? _signInCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyPropertyChangedFor(nameof(CancelText))]
    private bool _isBusy;

    [ObservableProperty] private string? _statusText;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? _errorText;

    public GoogleAccountViewModel(
        IGoogleAuth auth,
        IMailSessionFactory sessions,
        Func<MailAccount, Task> onConnected,
        Func<string, bool> isAccountKeyInUse,
        MailAccount? existing = null,
        Func<MailAccount, Task<bool>>? onRemove = null)
    {
        _auth = auth;
        _sessions = sessions;
        _onConnected = onConnected;
        _isAccountKeyInUse = isAccountKeyInUse;
        _existing = existing;
        _onRemove = onRemove;
    }

    public event EventHandler<bool>? CloseRequested;

    public bool IsReconnect => _existing is not null;
    public string Title => _existing is null ? "Add Google account" : "Reconnect Google account";
    public string Heading => _existing is null ? "Gmail or Google Workspace" : _existing.EmailAddress;
    public bool IsConfigured => _auth.IsConfigured;
    public string? ConfigurationProblem => _auth.ConfigurationProblem;
    public bool HasError => ErrorText is not null;
    public bool CanRemove => _existing is not null && _onRemove is not null;
    public string CancelText => IsBusy ? "Cancel sign-in" : "Close";

    public string PermissionsText =>
        "HCG Mail asks Google for:\n" +
        "\u2022 Read, compose and send email, and change labels (gmail.modify) \u2014 list, read, mark read, star, archive, move to Trash, drafts, send. " +
        "It does not include permanently deleting mail.\n" +
        "\u2022 Your name and e-mail address (openid, email, profile)\n" +
        "Google also lets HCG Mail renew the sign-in without asking again until you remove the account or revoke access at myaccount.google.com/permissions.";

    private bool CanSignIn() => !IsBusy && IsConfigured;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsBusy = true;
        ErrorText = null;
        StatusText = "Waiting for you to sign in to Google in your web browser\u2026";
        _signInCts = new CancellationTokenSource();
        GoogleSignIn? signIn = null;
        var keepTokens = false;
        try
        {
            signIn = await _auth.SignInAsync(_existing?.Username, _signInCts.Token);

            if (_existing is not null && !string.Equals(signIn.AccountKey, _existing.GoogleAccountId, StringComparison.Ordinal))
            {
                ErrorText = $"You signed in as {signIn.Email}, but this account is {_existing.EmailAddress}. Reconnect with {_existing.EmailAddress}, " +
                            $"or add {signIn.Email} as a separate account.";
                return;
            }
            if (_existing is null && _isAccountKeyInUse(signIn.AccountKey))
            {
                keepTokens = true; // the token belongs to the account that is already added
                ErrorText = $"{signIn.Email} is already added to HCG Mail.";
                return;
            }

            StatusText = "Signed in. Checking Gmail\u2026";
            var account = new MailAccount
            {
                Id = _existing?.Id ?? MailAccount.NewId(),
                DisplayName = _existing?.DisplayName is { Length: > 0 } name ? name : signIn.DisplayName ?? signIn.Email,
                EmailAddress = signIn.Email,
                Username = signIn.Email,
                Imap = MailAccount.GmailEndpoint,
                Smtp = MailAccount.GmailEndpoint,
                Provider = MailProviderKind.Gmail,
                GoogleAccountId = signIn.AccountKey,
            };
            await using (var session = _sessions.Create(account, ""))
            {
                await session.ConnectAsync(_signInCts.Token);
                if (session is GmailMailSession gmail && await gmail.GetProfileAsync(_signInCts.Token) is { Length: > 0 } address)
                    account = account with { EmailAddress = address };
            }

            keepTokens = true;
            await _onConnected(account);
            CloseRequested?.Invoke(this, true);
        }
        catch (MailOperationException ex)
        {
            ErrorText = ex.Kind == MailErrorKind.Cancelled
                ? (IsReconnect ? "Sign-in was cancelled. The account was not changed." : "Sign-in was cancelled. No account was added.")
                : ex.UserMessage;
        }
        finally
        {
            // A token from a sign-in that did not result in a usable account is deleted again,
            // unless it belongs to an account that is already configured.
            if (signIn is not null && !keepTokens && !_isAccountKeyInUse(signIn.AccountKey))
            {
                try
                {
                    await _auth.SignOutAsync(signIn.AccountKey);
                }
                catch (Exception)
                {
                    // Best effort; the token stays in the protected store only.
                }
            }
            _signInCts?.Dispose();
            _signInCts = null;
            IsBusy = false;
            StatusText = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_signInCts is { } cts)
        {
            cts.Cancel(); // stops waiting for the browser; nothing is saved
            return;
        }
        CloseRequested?.Invoke(this, false);
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (_existing is not null && _onRemove is not null && await _onRemove(_existing))
            CloseRequested?.Invoke(this, false);
    }
}
