using System.ComponentModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Mobile.Platform;

namespace HCG.Mail.Mobile.ViewModels;

/// <summary>The three mail screens. On iPhone one is shown at a time; on iPad they sit side by side.</summary>
public enum MailPage
{
    Mailboxes,
    Messages,
    Reader,
}

/// <summary>Chosen from the window's width (in points, after text scaling), so iPad split view and rotation are handled too.</summary>
public enum ShellLayout
{
    /// <summary>iPhone, or a narrow iPad split-view window: one screen with back navigation.</summary>
    Compact,

    /// <summary>iPad portrait or a half-width window: message list and reader; mailboxes slide over.</summary>
    Regular,

    /// <summary>iPad landscape: mailboxes, message list and reader.</summary>
    Wide,
}

/// <summary>
/// Navigation and layout around the shared mailbox coordinator (<see cref="MainWindowViewModel"/>), which still does
/// all the mail work: accounts, folders, paging, refresh, reading, compose and message actions.
/// </summary>
public sealed partial class MobileShellViewModel : ObservableObject
{
    public const double RegularMinWidth = 680;
    public const double WideMinWidth = 1000;
    public const double MaxTextScale = 1.6;

    private readonly IMobilePlatform _platform;
    private AccountViewModel? _watchedAccount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMailboxesPane), nameof(ShowListPane), nameof(ShowReaderPane), nameof(Title))]
    private MailPage _page = MailPage.Messages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompact), nameof(IsWide), nameof(ShowMailboxesPane), nameof(ShowListPane), nameof(ShowReaderPane),
        nameof(ShowMailboxesButton), nameof(ShowSheetAsCard))]
    private ShellLayout _layout = ShellLayout.Compact;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMailboxesPane))]
    private bool _isSidebarOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentPadding), nameof(TopInset))]
    private Thickness _safeArea;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentPadding), nameof(IsKeyboardVisible))]
    private double _keyboardHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentPadding), nameof(TopInset))]
    private double _textScale = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimationsEnabled), nameof(SpinRefreshIcon))]
    private bool _reduceMotion;

    public MobileShellViewModel(MainWindowViewModel mail, MobileOverlays overlays, IMobilePlatform platform)
    {
        Mail = mail;
        Overlays = overlays;
        _platform = platform;
        mail.ShareSavedAttachments = true;
        mail.RefreshHint = "pull down";
        mail.AutomaticRefreshQualifier = " while HCG Mail is open";
        ApplyAccessibility();
        platform.AccessibilitySettingsChanged += (_, _) => App.Services.UiThread.Post(ApplyAccessibility);

        mail.PropertyChanged += OnMailPropertyChanged;
        WatchAccount(mail.SelectedAccount);
        mail.Reader.PropertyChanged += OnReaderPropertyChanged;
        overlays.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MobileOverlays.Banner))
                DropBannerDuplicatingOfflineNotice();
        };
        mail.MessageList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MessageListViewModel.Folder))
                OnPropertyChanged(nameof(ShowOfflineNotice));
        };
    }

    public MainWindowViewModel Mail { get; }
    public MobileOverlays Overlays { get; }
    public string PlatformDescription => _platform.Description;

    public bool IsCompact => Layout == ShellLayout.Compact;
    public bool IsWide => Layout == ShellLayout.Wide;
    public bool AnimationsEnabled => !ReduceMotion;

    /// <summary>
    /// The connection was lost (no network, server unreachable) while a folder was already shown: the messages loaded
    /// earlier stay readable and a notice says so. Without a loaded folder the full-screen error state is shown instead.
    /// </summary>
    public bool ShowOfflineNotice => Mail.SelectedAccount?.HasError == true && Mail.MessageList.Folder is not null;

    public string OfflineNoticeText => Mail.SelectedAccount?.StatusText is { Length: > 0 } reason
        ? $"Not connected — showing messages loaded earlier. {reason}"
        : "Not connected — showing messages loaded earlier.";

    /// <summary>The refresh icon turns while mail is being checked, unless the user asked iOS to reduce motion.</summary>
    public bool SpinRefreshIcon => Mail.IsBusy && AnimationsEnabled;
    public bool IsKeyboardVisible => KeyboardHeight > 0;

    public bool ShowMailboxesPane => Layout switch
    {
        ShellLayout.Wide => true,
        ShellLayout.Regular => IsSidebarOpen,
        _ => Page == MailPage.Mailboxes,
    };

    public bool ShowListPane => Layout != ShellLayout.Compact || Page == MailPage.Messages;
    public bool ShowReaderPane => Layout != ShellLayout.Compact || Page == MailPage.Reader;

    /// <summary>The list header's mailboxes button: "back" on iPhone, a slide-over toggle on iPad portrait.</summary>
    public bool ShowMailboxesButton => Layout != ShellLayout.Wide;

    /// <summary>iPad shows sheets as a centred card over the mail; iPhone uses the whole screen.</summary>
    public bool ShowSheetAsCard => Layout != ShellLayout.Compact;

    public string Title => Page switch
    {
        MailPage.Mailboxes => "Mailboxes",
        MailPage.Reader => Mail.Reader.Subject,
        _ => Mail.MessageList.FolderTitle,
    };

    /// <summary>
    /// Safe area (notch, Dynamic Island, home indicator) plus the on-screen keyboard, so nothing is drawn underneath
    /// them. The platform reports points; the interface is scaled by <see cref="TextScale"/>, so the padding is divided.
    /// </summary>
    public Thickness ContentPadding => new(
        SafeArea.Left / TextScale, SafeArea.Top / TextScale, SafeArea.Right / TextScale, Math.Max(SafeArea.Bottom, KeyboardHeight) / TextScale);

    public double TopInset => SafeArea.Top / TextScale;

    /// <summary>Called by the view whenever the available size changes (rotation, split view, text size).</summary>
    public void UpdateSize(double width, double height)
    {
        var layout = width >= WideMinWidth ? ShellLayout.Wide : width >= RegularMinWidth ? ShellLayout.Regular : ShellLayout.Compact;
        if (layout == Layout)
            return;
        Layout = layout;
        IsSidebarOpen = false;
        // Going back to one screen: show the reader only if a message is actually open.
        if (layout == ShellLayout.Compact && Page == MailPage.Reader && !Mail.Reader.HasMessage)
            Page = MailPage.Messages;
    }

    public static double ClampTextScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, 1, MaxTextScale) : 1;

    private void ApplyAccessibility()
    {
        TextScale = ClampTextScale(_platform.TextScale);
        ReduceMotion = _platform.PrefersReducedMotion;
    }

    // ---- navigation -------------------------------------------------------------------------

    /// <summary>A folder was tapped in the mailbox list.</summary>
    public void OnFolderChosen()
    {
        IsSidebarOpen = false;
        if (IsCompact)
            Page = MailPage.Messages;
    }

    /// <summary>A message was tapped (also when it was already selected, so tapping it again reopens it).</summary>
    public void OnMessageChosen()
    {
        if (Mail.MessageList.SelectedItem is null)
            return;
        if (IsCompact)
            Page = MailPage.Reader;
    }

    [RelayCommand]
    private void ShowMailboxes()
    {
        if (Layout == ShellLayout.Regular)
            IsSidebarOpen = !IsSidebarOpen;
        else
            Page = MailPage.Mailboxes;
    }

    [RelayCommand]
    private void CloseSidebar() => IsSidebarOpen = false;

    [RelayCommand]
    private void BackToMessages() => Page = MailPage.Messages;

    /// <summary>Back gesture or button: closes the top-most thing first (prompt, sheet, slide-over), then goes up one screen.</summary>
    public async Task<bool> GoBackAsync()
    {
        if (Overlays.Prompt is { } prompt)
        {
            prompt.CancelCommand.Execute(null);
            return true;
        }
        if (Overlays.CurrentSheet is { } sheet)
        {
            await CloseSheetAsync(sheet);
            return true;
        }
        if (IsSidebarOpen)
        {
            IsSidebarOpen = false;
            return true;
        }
        if (IsCompact && Page == MailPage.Reader)
        {
            Page = MailPage.Messages;
            return true;
        }
        if (IsCompact && Page == MailPage.Messages)
        {
            Page = MailPage.Mailboxes;
            return true;
        }
        return false;
    }

    /// <summary>"Done" / "Cancel" on a sheet. An unsent message asks first, exactly as on the desktop.</summary>
    [RelayCommand]
    private async Task CloseSheetAsync(object? sheet)
    {
        sheet ??= Overlays.CurrentSheet;
        switch (sheet)
        {
            case null:
                return;
            case ComposeViewModel { IsDirty: true } compose:
                if (await compose.ConfirmCloseAsync())
                    Overlays.CloseSheet(compose);
                return;
            case AccountSetupViewModel setup:
                setup.CancelCommand.Execute(null);
                return;
            case MicrosoftAccountViewModel microsoft:
                // Cancels a pending browser sign-in as well.
                microsoft.CancelCommand.Execute(null);
                return;
            default:
                Overlays.CloseSheet(sheet);
                return;
        }
    }

    // ---- lifecycle ---------------------------------------------------------------------------

    /// <summary>iOS moved the app to the background. Nothing runs there; see <see cref="MainWindowViewModel.Suspend"/>.</summary>
    public void OnEnteredBackground() => Mail.Suspend();

    public Task OnReturnedToForegroundAsync() => Mail.ResumeAsync();

    private void OnMailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsBusy))
            OnPropertyChanged(nameof(SpinRefreshIcon));
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedAccount))
            WatchAccount(Mail.SelectedAccount);
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedAccount) && Mail.SelectedAccount is null && Page == MailPage.Reader)
            Page = MailPage.Messages;
    }

    private void WatchAccount(AccountViewModel? account)
    {
        if (_watchedAccount is not null)
            _watchedAccount.PropertyChanged -= OnAccountPropertyChanged;
        _watchedAccount = account;
        if (account is not null)
            account.PropertyChanged += OnAccountPropertyChanged;
        OnAccountPropertyChanged(null, new PropertyChangedEventArgs(null));
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(AccountViewModel.HasError) or nameof(AccountViewModel.StatusText))
        {
            OnPropertyChanged(nameof(ShowOfflineNotice));
            OnPropertyChanged(nameof(OfflineNoticeText));
            DropBannerDuplicatingOfflineNotice();
        }
    }

    /// <summary>The offline notice already explains a failed refresh; an error banner with the same text would cover it.</summary>
    private void DropBannerDuplicatingOfflineNotice()
    {
        if (ShowOfflineNotice && Overlays.Banner is { IsError: true } banner
            && string.Equals(banner.Message, Mail.SelectedAccount?.StatusText, StringComparison.Ordinal))
            Overlays.DismissBannerCommand.Execute(null);
    }

    private void OnReaderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessageReaderViewModel.Subject))
            OnPropertyChanged(nameof(Title));
        // The open message was deleted or moved and nothing replaced it: return to the list on iPhone.
        if (e.PropertyName == nameof(MessageReaderViewModel.ShowPlaceholder) && IsCompact && Page == MailPage.Reader && Mail.Reader.ShowPlaceholder)
            Page = MailPage.Messages;
    }
}
