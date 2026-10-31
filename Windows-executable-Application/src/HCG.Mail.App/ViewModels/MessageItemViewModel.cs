using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HCG.Mail.Core.Mail;

namespace HCG.Mail.App.ViewModels;

public sealed partial class MessageItemViewModel : ObservableObject
{
    private readonly bool _showRecipients;

    [ObservableProperty] private bool _isRead;
    [ObservableProperty] private bool _isFlagged;
    [ObservableProperty] private bool _isAnswered;

    public MessageItemViewModel(MessageSummary summary, bool showRecipients)
    {
        _showRecipients = showRecipients;
        Summary = summary;
        Apply(summary);
    }

    public MessageSummary Summary { get; private set; }
    public MessageRef Ref => Summary.Ref;

    public string Sender => _showRecipients
        ? "To: " + (Summary.To.Count == 0 ? "(no recipients)" : string.Join(", ", Summary.To.Select(a => a.DisplayName)))
        : Summary.From?.DisplayName ?? "(unknown sender)";

    public string SenderAddress => _showRecipients
        ? string.Join(", ", Summary.To.Select(a => a.Address))
        : Summary.From?.Address ?? "";

    public string Subject => string.IsNullOrWhiteSpace(Summary.Subject) ? "(no subject)" : Summary.Subject;
    public string Preview => Summary.Preview;
    public bool HasPreview => Summary.Preview.Length > 0;
    public bool HasAttachments => Summary.HasAttachments;
    public string DateText => FormatListDate(Summary.Date, DateTimeOffset.Now, CultureInfo.CurrentCulture);
    public string FullDateText => Summary.Date.ToLocalTime().ToString("dddd d MMMM yyyy HH:mm", CultureInfo.CurrentCulture);

    public void Apply(MessageSummary summary)
    {
        Summary = summary;
        IsRead = summary.Flags.HasFlag(MessageFlags.Seen);
        IsFlagged = summary.Flags.HasFlag(MessageFlags.Flagged);
        IsAnswered = summary.Flags.HasFlag(MessageFlags.Answered);
        OnPropertyChanged(string.Empty);
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        bool Has(string? s) => s is not null && compare.IndexOf(s, query.Trim(), options) >= 0;
        return Has(Summary.Subject) || Has(Summary.From?.Name) || Has(Summary.From?.Address) || Has(Summary.Preview)
               || Summary.To.Any(a => Has(a.Name) || Has(a.Address));
    }

    public static string FormatListDate(DateTimeOffset date, DateTimeOffset now, CultureInfo culture)
    {
        if (date == DateTimeOffset.MinValue)
            return "";
        var local = date.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today)
            return local.ToString("HH:mm", culture);
        if (local.Date == today.AddDays(-1))
            return "Yesterday";
        if (local.Date > today.AddDays(-7) && local.Date < today)
            return local.ToString("ddd", culture);
        if (local.Year == today.Year)
            return local.ToString("d MMM", culture);
        return local.ToString("dd.MM.yyyy", culture);
    }
}

public sealed class AttachmentViewModel
{
    public AttachmentViewModel(AttachmentInfo info, Func<AttachmentViewModel, Task> save)
    {
        Info = info;
        SaveCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => save(this));
    }

    public AttachmentInfo Info { get; }
    public string Name => Info.SafeFileName;
    public bool IsDangerous => Info.IsPotentiallyDangerous;
    public string SizeText => Info.SizeEstimate is { } s ? FormatSize(s) : "";
    public string ToolTip => IsDangerous
        ? $"{Info.ContentType}\nCaution: this type of file can run code on your computer. HCG Mail never opens attachments; only open it if you trust the sender."
        : Info.ContentType;
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand SaveCommand { get; }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}
