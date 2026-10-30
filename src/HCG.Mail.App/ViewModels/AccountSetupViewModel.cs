using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Services;

namespace HCG.Mail.App.ViewModels;

public sealed record SecurityOption(MailSecurityMode Mode, string Label)
{
    public static IReadOnlyList<SecurityOption> All { get; } =
    [
        new(MailSecurityMode.SslTls, "SSL/TLS (recommended)"),
        new(MailSecurityMode.StartTls, "STARTTLS (required)"),
    ];

    public override string ToString() => Label;
}

public sealed partial class ConnectionStepViewModel(ConnectionTestStepKind kind) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPassed), nameof(IsFailed), nameof(IsRunning), nameof(IsPending), nameof(IsSkipped))]
    private ConnectionTestStatus _status = ConnectionTestStatus.Pending;

    [ObservableProperty] private string _detail = "";

    public ConnectionTestStepKind Kind { get; } = kind;
    public string Title { get; } = new ConnectionTestStep(kind, ConnectionTestStatus.Pending, "").Title;
    public bool IsPassed => Status == ConnectionTestStatus.Passed;
    public bool IsFailed => Status == ConnectionTestStatus.Failed;
    public bool IsRunning => Status == ConnectionTestStatus.Running;
    public bool IsPending => Status == ConnectionTestStatus.Pending;
    public bool IsSkipped => Status == ConnectionTestStatus.Skipped;
}

/// <summary>Add or edit an IMAP/SMTP account, test it step by step, and store the password in the OS vault.</summary>
public sealed partial class AccountSetupViewModel : ObservableObject
{
    private readonly IConnectionTester _tester;
    private readonly ICredentialVault _vault;
    private readonly Func<MailAccount, Task> _onSaved;
    private readonly Func<MailAccount, Task>? _onRemove;
    private readonly MailAccount? _existing;
    private bool _usernameEdited;
    private bool _validated;
    private CancellationTokenSource? _testCts;

    [ObservableProperty] private ProviderPreset _selectedPreset;
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _emailAddress = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _imapHost = "";
    [ObservableProperty] private string _imapPort = PortDefaults.ImapSslTls.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private SecurityOption _imapSecurity = SecurityOption.All[0];
    [ObservableProperty] private string _smtpHost = "";
    [ObservableProperty] private string _smtpPort = PortDefaults.SmtpSslTls.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private SecurityOption _smtpSecurity = SecurityOption.All[0];

    [ObservableProperty] private string? _emailError;
    [ObservableProperty] private string? _displayNameError;
    [ObservableProperty] private string? _usernameError;
    [ObservableProperty] private string? _passwordError;
    [ObservableProperty] private string? _imapHostError;
    [ObservableProperty] private string? _imapPortError;
    [ObservableProperty] private string? _smtpHostError;
    [ObservableProperty] private string? _smtpPortError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand), nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(TestButtonText))]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestConnectionCommand))]
    private bool _isSaving;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTestSummary))] private string? _testSummary;
    [ObservableProperty] private bool _testSucceeded;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFormError))] private string? _formError;

    public AccountSetupViewModel(
        IConnectionTester tester,
        ICredentialVault vault,
        Func<MailAccount, Task> onSaved,
        MailAccount? existing = null,
        Func<MailAccount, Task>? onRemove = null,
        string? message = null)
    {
        _tester = tester;
        _vault = vault;
        _onSaved = onSaved;
        _onRemove = onRemove;
        _existing = existing;
        IntroMessage = message;
        Steps = new ObservableCollection<ConnectionStepViewModel>(Enum.GetValues<ConnectionTestStepKind>().Select(k => new ConnectionStepViewModel(k)));

        _selectedPreset = ProviderPresets.Find(existing?.PresetId) ?? ProviderPresets.DomeneNo;
        if (existing is not null)
        {
            var draft = AccountValidator.FromAccount(existing);
            _displayName = draft.DisplayName;
            _emailAddress = draft.EmailAddress;
            _username = draft.Username;
            _imapHost = draft.ImapHost;
            _imapPort = draft.ImapPort;
            _imapSecurity = SecurityOption.All.First(o => o.Mode == draft.ImapSecurity);
            _smtpHost = draft.SmtpHost;
            _smtpPort = draft.SmtpPort;
            _smtpSecurity = SecurityOption.All.First(o => o.Mode == draft.SmtpSecurity);
            _usernameEdited = !string.Equals(draft.Username, draft.EmailAddress, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsEditing => _existing is not null;
    public string Title => IsEditing ? "Account settings" : "Add email account";
    public string? IntroMessage { get; }
    public bool HasIntroMessage => IntroMessage is not null;
    public IReadOnlyList<ProviderPreset> Presets => ProviderPresets.All;
    public IReadOnlyList<SecurityOption> SecurityOptions => SecurityOption.All;
    public ObservableCollection<ConnectionStepViewModel> Steps { get; }
    public string HostnameHint => SelectedPreset.HostnameHint;
    public bool HasTestSummary => TestSummary is not null;
    public bool HasFormError => FormError is not null;
    public bool CanRemove => IsEditing && _onRemove is not null;
    public string TestButtonText => IsTesting ? "Cancel test" : "Test connection";
    public string PasswordWatermark => IsEditing ? "Leave blank to keep the saved password" : "Password";

    public string CredentialStorageNote => _vault.IsPersistent
        ? $"Your password is stored only in {_vault.StorageDescription}. It is never written to a file or sent anywhere except your own mail servers."
        : "Secure password storage is not available on this platform, so your password is kept in memory for this session only. You will be asked for it again next time HCG Mail starts.";

    /// <summary>Raised with true after a successful save, false on cancel.</summary>
    public event EventHandler<bool>? CloseRequested;

    partial void OnSelectedPresetChanged(ProviderPreset value)
    {
        ImapPort = value.ImapPort.ToString(CultureInfo.InvariantCulture);
        ImapSecurity = SecurityOption.All.First(o => o.Mode == value.ImapSecurity);
        SmtpPort = value.SmtpPort.ToString(CultureInfo.InvariantCulture);
        SmtpSecurity = SecurityOption.All.First(o => o.Mode == value.SmtpSecurity);
        OnPropertyChanged(nameof(HostnameHint));
    }

    partial void OnEmailAddressChanged(string? oldValue, string newValue)
    {
        if (!_usernameEdited || string.Equals(Username, oldValue?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Username = newValue.Trim();
            _usernameEdited = false;
        }
        Revalidate();
    }

    partial void OnUsernameChanged(string value)
    {
        if (!string.Equals(value, EmailAddress.Trim(), StringComparison.OrdinalIgnoreCase))
            _usernameEdited = true;
        Revalidate();
    }

    partial void OnImapSecurityChanged(SecurityOption value)
    {
        if (ImapPort == PortDefaults.Imap(Other(value.Mode)).ToString(CultureInfo.InvariantCulture))
            ImapPort = PortDefaults.Imap(value.Mode).ToString(CultureInfo.InvariantCulture);
    }

    partial void OnSmtpSecurityChanged(SecurityOption value)
    {
        if (SmtpPort == PortDefaults.Smtp(Other(value.Mode)).ToString(CultureInfo.InvariantCulture))
            SmtpPort = PortDefaults.Smtp(value.Mode).ToString(CultureInfo.InvariantCulture);
    }

    partial void OnImapHostChanged(string value)
    {
        // Most providers use one server name for both directions; prefill SMTP until the user edits it.
        if (string.IsNullOrWhiteSpace(SmtpHost) || SmtpHost == _lastAutoSmtpHost)
        {
            SmtpHost = value.Trim();
            _lastAutoSmtpHost = SmtpHost;
        }
        Revalidate();
    }

    private string? _lastAutoSmtpHost;

    partial void OnDisplayNameChanged(string value) => Revalidate();
    partial void OnPasswordChanged(string value) => Revalidate();
    partial void OnImapPortChanged(string value) => Revalidate();
    partial void OnSmtpHostChanged(string value) => Revalidate();
    partial void OnSmtpPortChanged(string value) => Revalidate();

    private bool CanTest() => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        if (IsTesting)
        {
            _testCts?.Cancel();
            return;
        }
        if (!ValidateAll())
            return;
        var password = await ResolvePasswordAsync();
        if (password is null)
        {
            PasswordError = "Enter your password to test the connection.";
            return;
        }

        var account = AccountValidator.ToAccount(BuildDraft(), _existing?.Id ?? "connection-test", SelectedPreset.Id);
        foreach (var step in Steps)
        {
            step.Status = ConnectionTestStatus.Pending;
            step.Detail = "";
        }
        TestSummary = null;
        IsTesting = true;
        _testCts = new CancellationTokenSource();
        var progress = new Progress<ConnectionTestStep>(step =>
        {
            var vm = Steps.First(s => s.Kind == step.Kind);
            vm.Status = step.Status;
            vm.Detail = step.Detail;
        });

        try
        {
            var report = await _tester.RunAsync(account, password, progress, _testCts.Token);
            foreach (var step in report.Steps)
            {
                var vm = Steps.First(s => s.Kind == step.Kind);
                vm.Status = step.Status;
                vm.Detail = step.Detail;
            }
            TestSucceeded = report.Succeeded;
            TestSummary = report.Succeeded
                ? "All checks passed. HCG Mail can receive and send mail with these settings."
                : "Some checks did not pass. Review the details above, correct the settings and test again.";
        }
        catch (OperationCanceledException)
        {
            TestSucceeded = false;
            TestSummary = "Connection test cancelled.";
            foreach (var step in Steps.Where(s => s.IsRunning || s.IsPending))
                step.Status = ConnectionTestStatus.Skipped;
        }
        finally
        {
            IsTesting = false;
            _testCts.Dispose();
            _testCts = null;
        }
    }

    private bool CanSave() => !IsTesting && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        FormError = null;
        if (!ValidateAll())
            return;
        IsSaving = true;
        try
        {
            var account = AccountValidator.ToAccount(BuildDraft(), _existing?.Id, SelectedPreset.Id);
            if (!string.IsNullOrEmpty(Password))
                await _vault.SetAsync(account.CredentialKey, Password);
            Password = "";
            await _onSaved(account);
            CloseRequested?.Invoke(this, true);
        }
        catch (CredentialVaultException ex)
        {
            FormError = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _testCts?.Cancel();
        Password = "";
        CloseRequested?.Invoke(this, false);
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (_existing is null || _onRemove is null)
            return;
        await _onRemove(_existing);
        CloseRequested?.Invoke(this, false);
    }

    private async Task<string?> ResolvePasswordAsync()
    {
        if (!string.IsNullOrEmpty(Password))
            return Password;
        return _existing is null ? null : await _vault.GetAsync(_existing.CredentialKey);
    }

    private AccountDraft BuildDraft() => new()
    {
        DisplayName = DisplayName,
        EmailAddress = EmailAddress,
        Username = Username,
        Password = Password,
        ImapHost = ImapHost,
        ImapPort = ImapPort,
        ImapSecurity = ImapSecurity.Mode,
        SmtpHost = SmtpHost,
        SmtpPort = SmtpPort,
        SmtpSecurity = SmtpSecurity.Mode,
    };

    private bool ValidateAll()
    {
        _validated = true;
        var errors = AccountValidator.Validate(BuildDraft(), requirePassword: !IsEditing);
        string? Error(AccountField field) => errors.FirstOrDefault(e => e.Field == field)?.Message;
        EmailError = Error(AccountField.EmailAddress);
        DisplayNameError = Error(AccountField.DisplayName);
        UsernameError = Error(AccountField.Username);
        PasswordError = Error(AccountField.Password);
        ImapHostError = Error(AccountField.ImapHost);
        ImapPortError = Error(AccountField.ImapPort);
        SmtpHostError = Error(AccountField.SmtpHost);
        SmtpPortError = Error(AccountField.SmtpPort);
        return errors.Count == 0;
    }

    private void Revalidate()
    {
        if (_validated)
            ValidateAll();
    }

    private static MailSecurityMode Other(MailSecurityMode mode) =>
        mode == MailSecurityMode.SslTls ? MailSecurityMode.StartTls : MailSecurityMode.SslTls;
}
