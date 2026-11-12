using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Services;
using HCG.Mail.Email.Graph;

namespace HCG.Mail.App.ViewModels;

/// <summary>
/// Adds or reconnects a Microsoft (Outlook.com / Hotmail / Microsoft 365) account: sign-in happens on Microsoft's
/// page in the system browser, then HCG Mail checks that the mailbox is reachable before saving anything.
/// </summary>
public sealed partial class MicrosoftAccountViewModel : ObservableObject
{
    public const string PrivacyText =
        "You sign in on Microsoft's own page in your web browser. HCG Mail never sees your Microsoft password and stores no password; " +
        "Microsoft gives HCG Mail a sign-in token that is kept in your computer's protected storage.";

    public const string RoleText =
        "Microsoft access covers this mailbox only. It gives no roles or permissions in HCG Mail, HCG Connect or any other HCG system.";

    public const string SupportText =
        "Works with personal Outlook.com, Hotmail and Live accounts, and Microsoft 365 work or school mailboxes in Exchange Online. " +
        "Not supported: mailboxes on on-premises Exchange servers, shared mailboxes, and accounts without an Exchange Online mailbox.";

    public const string TenantText =
        "Some organisations only allow apps an administrator has approved. If Microsoft says admin approval is required, ask your " +
        "Microsoft 365 administrator to grant consent to HCG Mail for Mail.ReadWrite and Mail.Send.";

    private readonly IMicrosoftAuth _auth;
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

    public MicrosoftAccountViewModel(
        IMicrosoftAuth auth,
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
    public string Title => _existing is null ? "Add Microsoft account" : "Reconnect Microsoft account";
    public string Heading => _existing is null ? "Outlook.com, Hotmail or Microsoft 365" : _existing.EmailAddress;
    public bool IsConfigured => _auth.IsConfigured;
    public string? ConfigurationProblem => _auth.ConfigurationProblem;
    public bool HasError => ErrorText is not null;
    public bool CanRemove => _existing is not null && _onRemove is not null;
    public string CancelText => IsBusy ? "Cancel sign-in" : "Close";

    public string PermissionsText =>
        "HCG Mail asks Microsoft for these delegated permissions:\n" +
        "• Read and change your mail (Mail.ReadWrite) — list, read, flag, move, delete and save drafts\n" +
        "• Send mail as you (Mail.Send)\n" +
        "• Your name and e-mail address (User.Read)\n" +
        "Microsoft also lets HCG Mail renew the sign-in without asking again (offline access) until you remove the account or revoke access.";

    private bool CanSignIn() => !IsBusy && IsConfigured;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsBusy = true;
        ErrorText = null;
        StatusText = "Waiting for you to sign in to Microsoft in your web browser…";
        _signInCts = new CancellationTokenSource();
        MicrosoftSignIn? signIn = null;
        var keepTokens = false;
        try
        {
            signIn = await _auth.SignInAsync(_existing?.Username, _signInCts.Token);

            if (_existing is not null && !string.Equals(signIn.Username, _existing.Username, StringComparison.OrdinalIgnoreCase))
            {
                ErrorText = $"You signed in as {signIn.Username}, but this account is {_existing.Username}. Reconnect with {_existing.Username}, " +
                            $"or add {signIn.Username} as a separate account.";
                return;
            }
            if (_existing is null && _isAccountKeyInUse(signIn.AccountKey))
            {
                keepTokens = true; // they belong to the account that is already added
                ErrorText = $"{signIn.Username} is already added to HCG Mail.";
                return;
            }

            StatusText = "Signed in. Checking the mailbox…";
            var account = new MailAccount
            {
                Id = _existing?.Id ?? MailAccount.NewId(),
                DisplayName = _existing?.DisplayName is { Length: > 0 } name ? name : signIn.DisplayName ?? signIn.Username,
                EmailAddress = signIn.Username,
                Username = signIn.Username,
                Imap = MailAccount.GraphEndpoint,
                Smtp = MailAccount.GraphEndpoint,
                Provider = MailProviderKind.MicrosoftGraph,
                MicrosoftAccountId = signIn.AccountKey,
            };
            await using (var session = _sessions.Create(account, ""))
            {
                await session.ConnectAsync(_signInCts.Token);
                if (session is GraphMailSession graph)
                {
                    var profile = await graph.GetProfileAsync(_signInCts.Token);
                    account = account with
                    {
                        EmailAddress = string.IsNullOrWhiteSpace(profile.Mail) ? account.EmailAddress : profile.Mail,
                        DisplayName = _existing?.DisplayName is { Length: > 0 } ? account.DisplayName : profile.DisplayName ?? account.DisplayName,
                    };
                }
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
            // Tokens from a sign-in that did not result in a usable account are removed again,
            // unless they belong to an account that is already configured.
            if (signIn is not null && !keepTokens && !_isAccountKeyInUse(signIn.AccountKey))
            {
                try
                {
                    await _auth.SignOutAsync(signIn.AccountKey);
                }
                catch (Exception)
                {
                    // Best effort; the tokens stay in the protected cache only.
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
