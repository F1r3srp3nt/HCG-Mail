using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.App.Services;

namespace HCG.Mail.Mobile.ViewModels;

/// <summary>An alert-style question (iOS UIAlertController equivalent) drawn inside the app.</summary>
public sealed partial class PromptViewModel : ObservableObject
{
    private readonly TaskCompletionSource<DialogChoice> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PromptViewModel(string title, string message, string primaryText, string? secondaryText, string cancelText, bool destructive)
    {
        Title = title;
        Message = message;
        PrimaryText = primaryText;
        SecondaryText = secondaryText;
        CancelText = cancelText;
        IsDestructive = destructive;
    }

    public string Title { get; }
    public string Message { get; }
    public string PrimaryText { get; }
    public string? SecondaryText { get; }
    public string CancelText { get; }
    public bool IsDestructive { get; }
    public bool HasSecondary => SecondaryText is not null;
    public Task<DialogChoice> Result => _result.Task;

    [RelayCommand] private void Primary() => _result.TrySetResult(DialogChoice.Primary);
    [RelayCommand] private void Secondary() => _result.TrySetResult(DialogChoice.Secondary);
    [RelayCommand] private void Cancel() => _result.TrySetResult(DialogChoice.Cancel);
}

public enum BannerKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A short in-app notice at the top of the screen. Tapping it runs its action (for example, share a file).</summary>
public sealed partial class BannerViewModel(BannerKind kind, string title, string message, Action? action) : ObservableObject
{
    public BannerKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Message { get; } = message;
    public bool IsError => Kind == BannerKind.Error;
    public bool IsWarning => Kind == BannerKind.Warning;
    public bool IsSuccess => Kind == BannerKind.Success;
    public bool HasAction => action is not null;
    internal Action? Action => action;
}

/// <summary>A page shown above the mail screens (compose, account setup, settings…), full screen on iPhone and as a sheet on iPad.</summary>
public sealed class SheetEntry(object content)
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public object Content { get; } = content;
    public Task Closed => _closed.Task;
    internal void MarkClosed() => _closed.TrySetResult();
}

/// <summary>
/// Sheets, prompts and banners. Kept separate from the mail shell so the platform services (dialogs, notifications)
/// can be created before the mailbox coordinator that uses them.
/// </summary>
public sealed partial class MobileOverlays : ObservableObject
{
    private readonly Queue<PromptViewModel> _waitingPrompts = new();
    private DispatcherTimer? _bannerTimer;

    public ObservableCollection<SheetEntry> Sheets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrompt))]
    private PromptViewModel? _prompt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBanner))]
    private BannerViewModel? _banner;

    public MobileOverlays()
    {
        Sheets.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CurrentSheet));
            OnPropertyChanged(nameof(HasSheet));
        };
    }

    public object? CurrentSheet => Sheets.Count > 0 ? Sheets[^1].Content : null;
    public bool HasSheet => Sheets.Count > 0;
    public bool HasPrompt => Prompt is not null;
    public bool HasBanner => Banner is not null;

    /// <summary>How long a banner stays. Errors stay longer and can always be dismissed by tapping.</summary>
    public static TimeSpan BannerDuration(BannerKind kind) => kind is BannerKind.Error or BannerKind.Warning ? TimeSpan.FromSeconds(9) : TimeSpan.FromSeconds(5);

    public SheetEntry PushSheet(object content)
    {
        var entry = new SheetEntry(content);
        Sheets.Add(entry);
        return entry;
    }

    public void CloseSheet(object content)
    {
        var entry = Sheets.LastOrDefault(s => ReferenceEquals(s.Content, content));
        if (entry is null)
            return;
        Sheets.Remove(entry);
        entry.MarkClosed();
    }

    public async Task<DialogChoice> AskAsync(PromptViewModel prompt)
    {
        if (Prompt is null)
            Prompt = prompt;
        else
            _waitingPrompts.Enqueue(prompt);
        var choice = await prompt.Result;
        Prompt = _waitingPrompts.Count > 0 ? _waitingPrompts.Dequeue() : null;
        return choice;
    }

    public void ShowBanner(BannerViewModel banner)
    {
        Banner = banner;
        _bannerTimer?.Stop();
        _bannerTimer = new DispatcherTimer { Interval = BannerDuration(banner.Kind) };
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer?.Stop();
            if (ReferenceEquals(Banner, banner))
                Banner = null;
        };
        _bannerTimer.Start();
    }

    [RelayCommand]
    private void ActivateBanner()
    {
        var banner = Banner;
        Banner = null;
        banner?.Action?.Invoke();
    }

    [RelayCommand]
    private void DismissBanner() => Banner = null;
}
