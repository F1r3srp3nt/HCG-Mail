using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;

namespace HCG.Mail.App.ViewModels;

/// <summary>The centre pane: messages of the selected folder, newest first, with client-side search.</summary>
public sealed partial class MessageListViewModel : ObservableObject
{
    public const int PageSize = 150;

    private readonly List<MessageItemViewModel> _all = [];
    private IMailSession? _session;
    private int _version;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(EmptyText))]
    private string _searchText = "";

    [ObservableProperty] private MessageItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowList))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasError), nameof(ShowList))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadOlderCommand))]
    private bool _hasOlder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _unreadCount;

    [ObservableProperty] private string _folderTitle = "";

    public ObservableCollection<MessageItemViewModel> Items { get; } = [];

    public FolderViewModel? Folder
    {
        get => _folder;
        private set
        {
            if (SetProperty(ref _folder, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    private FolderViewModel? _folder;

    public bool HasError => ErrorMessage is not null;
    public bool ShowList => ErrorMessage is null;
    public bool IsEmpty => !IsLoading && ErrorMessage is null && Items.Count == 0 && Folder is not null;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public string EmptyText => IsSearching
        ? $"No loaded messages match “{SearchText.Trim()}”."
        : "This folder is empty.";

    public string CountText
    {
        get
        {
            if (Folder is null)
                return "";
            var culture = CultureInfo.CurrentCulture;
            var total = TotalCount.ToString("N0", culture);
            var text = TotalCount == 1 ? "1 message" : $"{total} messages";
            if (UnreadCount > 0)
                text += $" · {UnreadCount.ToString("N0", culture)} unread";
            if (IsSearching)
                text += $" · {Items.Count} shown";
            else if (_all.Count < TotalCount)
                text += $" · newest {_all.Count} loaded";
            return text;
        }
    }

    public event EventHandler<MailOperationException>? LoadFailed;

    public async Task LoadAsync(IMailSession session, FolderViewModel folder)
    {
        _session = session;
        Folder = folder;
        FolderTitle = folder.DisplayName;
        _all.Clear();
        Items.Clear();
        SelectedItem = null;
        TotalCount = 0;
        UnreadCount = 0;
        HasOlder = false;
        await FetchAsync(PageSize, notifyNew: false);
    }

    /// <summary>Switches to a new session (after a reconnect) while keeping the loaded list and selection.</summary>
    public void UseSession(IMailSession session) => _session = session;

    /// <summary>Reloads the loaded window of messages, keeping selection. Returns the number of new unread messages.</summary>
    public Task<int> RefreshAsync(CancellationToken cancellationToken = default) =>
        _session is null || Folder is null ? Task.FromResult(0) : FetchAsync(Math.Max(PageSize, _all.Count), notifyNew: true, cancellationToken);

    /// <summary>True when the last load/refresh of the folder failed (the error has already been reported).</summary>
    public bool LastFetchFailed { get; private set; }

    public void Clear()
    {
        _version++;
        _session = null;
        Folder = null;
        FolderTitle = "";
        _all.Clear();
        Items.Clear();
        SelectedItem = null;
        ErrorMessage = null;
        IsLoading = false;
        HasOlder = false;
        TotalCount = 0;
        UnreadCount = 0;
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void ShowError(string message)
    {
        ErrorMessage = message;
    }

    public void Remove(MessageItemViewModel item)
    {
        var index = Items.IndexOf(item);
        _all.Remove(item);
        Items.Remove(item);
        TotalCount = Math.Max(0, TotalCount - 1);
        if (!item.IsRead)
            AdjustUnread(-1);
        if (Items.Count > 0)
            SelectedItem = Items[Math.Clamp(index, 0, Items.Count - 1)];
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
    }

    public void AdjustUnread(int delta)
    {
        UnreadCount = Math.Max(0, UnreadCount + delta);
        if (Folder is not null)
            Folder.UnreadCount = UnreadCount;
    }

    [RelayCommand(CanExecute = nameof(HasOlder))]
    private async Task LoadOlderAsync()
    {
        if (_session is null || Folder is null)
            return;
        var version = _version;
        try
        {
            var page = await _session.GetMessagesAsync(Folder.FullName, _all.Count, PageSize);
            if (version != _version)
                return;
            var known = new HashSet<uint>(_all.Select(m => m.Ref.Uid));
            foreach (var summary in page.Messages.Where(m => !known.Contains(m.Ref.Uid)))
                _all.Add(new MessageItemViewModel(summary, ShowRecipients));
            HasOlder = page.HasOlder;
            TotalCount = page.TotalCount;
            ApplyFilter();
        }
        catch (MailOperationException ex)
        {
            LoadFailed?.Invoke(this, ex);
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private bool ShowRecipients => Folder?.Role is FolderRole.Sent or FolderRole.Drafts;

    private async Task<int> FetchAsync(int count, bool notifyNew, CancellationToken cancellationToken = default)
    {
        if (_session is null || Folder is null)
            return 0;
        var version = ++_version;
        var folder = Folder;
        IsLoading = true;
        try
        {
            LastFetchFailed = false;
            var page = await _session.GetMessagesAsync(folder.FullName, 0, count, cancellationToken);
            if (version != _version)
                return 0;

            var existing = _all.ToDictionary(m => m.Ref);
            var updated = new List<MessageItemViewModel>(page.Messages.Count);
            var newUnread = 0;
            foreach (var summary in page.Messages)
            {
                if (existing.TryGetValue(summary.Ref, out var vm))
                {
                    vm.Apply(summary);
                }
                else
                {
                    vm = new MessageItemViewModel(summary, ShowRecipients);
                    if (notifyNew && !summary.Flags.HasFlag(MessageFlags.Seen))
                        newUnread++;
                }
                updated.Add(vm);
            }

            var selectedRef = SelectedItem?.Ref;
            _all.Clear();
            _all.AddRange(updated);
            TotalCount = page.TotalCount;
            UnreadCount = page.UnreadCount;
            folder.UnreadCount = page.UnreadCount;
            HasOlder = page.HasOlder;
            ErrorMessage = null;
            ApplyFilter();
            if (selectedRef is not null && SelectedItem is null)
                SelectedItem = Items.FirstOrDefault(i => i.Ref == selectedRef);
            return newUnread;
        }
        catch (MailOperationException ex)
        {
            if (version == _version)
            {
                LastFetchFailed = ex.Kind != MailErrorKind.Cancelled;
                if (_all.Count == 0)
                    ErrorMessage = ex.UserMessage;
                LoadFailed?.Invoke(this, ex);
            }
            return 0;
        }
        finally
        {
            if (version == _version)
                IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText;
        var visible = string.IsNullOrWhiteSpace(query) ? _all : _all.Where(m => m.Matches(query)).ToList();
        var selected = SelectedItem;
        CollectionSync.Sync(Items, visible);
        if (selected is not null && !Items.Contains(selected))
            SelectedItem = null;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CountText));
    }
}
