using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.KeyVault;
using RemoteConnectionManager.Storage;

namespace RemoteConnectionManager.App.ViewModels;

public enum StorageSectionKind
{
    Blobs,
    Overview,
}

public sealed record StorageSection(StorageSectionKind Kind, string Title, string Glyph, string IconBrushKey);

/// <summary>A container in the left hand list.</summary>
public sealed record ContainerRow(ContainerInfo Info)
{
    public string Name => Info.Name;
    public string Note => string.Join(" · ", new[]
    {
        Info.PublicAccess is "None" or "" ? null : $"public: {Info.PublicAccess}",
        Info.HasLegalHold ? "legal hold" : null,
        Info.HasImmutability ? "immutable" : null,
    }.Where(s => s is not null));
}

/// <summary>A folder level in the breadcrumb. An empty prefix is the container root.</summary>
public sealed record BreadcrumbItem(string Name, string Prefix);

/// <summary>A folder or blob in the listing.</summary>
public sealed class BlobEntryRow
{
    public BlobEntryRow(BlobEntry entry, bool isPending = false)
    {
        Entry = entry;
        IsPending = isPending;
    }

    public BlobEntry Entry { get; }
    /// <summary>A folder made here on an account without real folders, which exists only once a file is uploaded into it.</summary>
    public bool IsPending { get; }
    public string Name => Entry.Name;
    public string DisplayName => Entry.DisplayName;
    public bool IsFolder => Entry.IsFolder;
    public string Glyph => IsFolder ? "" : "";
    public string IconBrushKey => IsFolder ? "Icon.Folder" : "Icon.Default";
    public string SizeText => IsFolder ? string.Empty : BlobPaths.FormatSize(Entry.Size);
    public string ModifiedText => Entry.LastModified is { } m ? m.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty;
    public string TierText => IsFolder ? (IsPending ? "new, empty" : string.Empty) : Entry.AccessTier ?? string.Empty;
    public string TypeText => IsFolder ? "Folder" : Entry.ContentType ?? Entry.BlobType ?? string.Empty;
}

/// <summary>
/// An open Azure Storage account: its blob containers, a folder style listing with upload (including
/// drag and drop), download, new folder, rename and delete, plus an account overview. Signs in with Entra ID
/// only, so Storage Blob Data roles decide what works. Transfers run in the panel at the bottom while
/// browsing carries on.
/// </summary>
public sealed partial class StorageTabViewModel : TabViewModelBase, IDisposable
{
    private const int PageSize = 1000;
    private const int TransferParallelism = 4;
    private const int DeleteParallelism = 8;

    private readonly ShellViewModel _shell;
    private readonly StorageAccountConnection? _entry;
    private readonly string? _tenantId;
    private readonly string? _startContainer;
    private readonly AzureAuth _auth;
    private readonly ClipboardGuard _clipboard;
    private readonly IDialogService _dialogs;
    private readonly AppLog _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _progressTimer;
    private readonly List<BlobEntryRow> _allRows = new();
    /// <summary>Folders made on accounts without real folders, per container, until a file lands in them.</summary>
    private readonly Dictionary<string, HashSet<string>> _pendingFolders = new(StringComparer.Ordinal);
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _detailsCts;
    private BlobExplorerClient? _client;
    private object? _clientCredential;
    private string? _continuation;
    private IReadOnlyList<BlobEntryRow> _selection = Array.Empty<BlobEntryRow>();

    [ObservableProperty] private StorageSection _selectedSection;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = string.Empty;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _errorNeedsSignIn;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private StorageAccountOverview? _overview;
    [ObservableProperty] private IReadOnlyList<PropertyRow> _overviewRows = Array.Empty<PropertyRow>();
    [ObservableProperty] private IReadOnlyList<PropertyRow> _protectionRows = Array.Empty<PropertyRow>();
    [ObservableProperty] private ContainerRow? _selectedContainer;
    [ObservableProperty] private string _containerFilterText = string.Empty;
    [ObservableProperty] private string _currentPrefix = string.Empty;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _isListing;
    [ObservableProperty] private string _listingError = string.Empty;
    [ObservableProperty] private string _containersError = string.Empty;
    [ObservableProperty] private bool _containersLoaded;
    [ObservableProperty] private BlobEntryRow? _selectedEntry;
    [ObservableProperty] private IReadOnlyList<PropertyRow> _detailRows = Array.Empty<PropertyRow>();
    [ObservableProperty] private IReadOnlyList<PropertyRow> _metadataRows = Array.Empty<PropertyRow>();
    [ObservableProperty] private string _detailsText = string.Empty;

    public StorageAccountId AccountId { get; }

    public IReadOnlyList<StorageSection> Sections { get; } =
    [
        new(StorageSectionKind.Blobs, "Containers", "", "Icon.Folder"),
        new(StorageSectionKind.Overview, "Overview", "", "Icon.Storage"),
    ];

    private readonly List<ContainerRow> _allContainers = new();
    public ObservableCollection<ContainerRow> Containers { get; } = new();
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();
    public ObservableCollection<BlobEntryRow> Entries { get; } = new();
    public ObservableCollection<StorageJobViewModel> Jobs { get; } = new();

    public bool IsBlobs => SelectedSection.Kind == StorageSectionKind.Blobs;
    public bool IsOverview => SelectedSection.Kind == StorageSectionKind.Overview;
    public bool HasError => ErrorText.Length > 0;
    public bool HasListingError => ListingError.Length > 0;
    public bool HasContainersError => ContainersError.Length > 0;
    public bool HasContainer => SelectedContainer is not null;
    public bool HasSelection => _selection.Count > 0;
    public bool HasSelectedEntry => SelectedEntry is not null;
    public bool HasSingleSelection => _selection.Count == 1;
    public bool HasJobs => Jobs.Count > 0;
    public bool CanGoUp => SelectedContainer is not null && CurrentPrefix.Length > 0;
    public bool CanLoadMore => _continuation is not null && !IsListing;
    public bool IsHierarchical => Overview?.HierarchicalNamespace == true;
    public bool ShowBusyOverlay => IsBusy && Overview is null;
    public bool NeedsSignIn => _auth.NeedsSignIn || !_auth.IsSignedIn;
    public string AccountText => _auth.AccountName is not { } name ? "Not signed in to Azure"
        : _auth.IsPrompting ? $"{name}: waiting for sign in"
        : _auth.NeedsSignIn ? $"{name}: sign in expired"
        : name;
    public string NamespaceText => Overview is null ? string.Empty : IsHierarchical ? "Data Lake (real folders)" : "Flat namespace (virtual folders)";
    public string DropHint => SelectedContainer is null ? "Pick a container on the left." : $"Drop files or folders here to upload them to {Location}.";
    /// <summary>container/prefix, the way people say where something is.</summary>
    public string Location => SelectedContainer is null ? AccountId.AccountName : $"{SelectedContainer.Name}/{CurrentPrefix}".TrimEnd('/');

    public string ListingText
    {
        get
        {
            if (SelectedContainer is null) return string.Empty;
            var folders = _allRows.Count(r => r.IsFolder);
            var files = _allRows.Count - folders;
            var text = $"{folders} folder{StorageJobViewModel.Plural(folders)}, {files} file{StorageJobViewModel.Plural(files)}";
            if (_continuation is not null) text += " shown, more to load";
            if (FilterText.Length > 0) text += $" ({Entries.Count} match the filter)";
            if (_selection.Count > 1) text += $" · {_selection.Count} selected";
            return text;
        }
    }

    public string EmptyText => IsListing || SelectedContainer is null || HasListingError || Entries.Count > 0 ? string.Empty
        : FilterText.Length > 0 ? "Nothing loaded matches the filter."
        : "This folder is empty. Drop files here, or use Upload.";

    public string ContainersEmptyText => ContainersLoaded && _allContainers.Count == 0 && !HasContainersError
        ? "No containers yet. Press New container." : string.Empty;

    public override Guid? EntryId => _entry?.Id;
    public override bool IsLive => true;

    public StorageTabViewModel(StorageAccountId id, string? tenantId, string title, string? container, StorageAccountConnection? entry,
        ShellViewModel shell, AzureAuth auth, ClipboardGuard clipboard, IDialogService dialogs, AppLog log)
    {
        AccountId = id;
        _tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
        _startContainer = string.IsNullOrWhiteSpace(container) ? null : container.Trim();
        _entry = entry;
        _shell = shell;
        _auth = auth;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _log = log;
        Title = $"(STG) {title}";
        Glyph = "";
        IconBrushKey = "Icon.Storage";
        _selectedSection = Sections[0];

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _progressTimer.Tick += (_, _) => RefreshJobs();
        Jobs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasJobs));
            ClearFinishedJobsCommand.NotifyCanExecuteChanged();
        };
        _auth.Changed += OnAuthChanged;
        _ = LoadAccountAsync();
    }

    private void OnAuthChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnAuthChanged(sender, e));
            return;
        }
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(NeedsSignIn));
        if (IsBusy && _auth.IsPrompting)
            BusyText = "Waiting for you to sign in to Azure. Look for the Microsoft sign in window, it may be behind this one...";
    }

    // ---------- property plumbing ----------

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnListingErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasListingError));
        OnPropertyChanged(nameof(EmptyText));
    }
    partial void OnContainersErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasContainersError));
        OnPropertyChanged(nameof(ContainersEmptyText));
    }
    partial void OnContainersLoadedChanged(bool value) => OnPropertyChanged(nameof(ContainersEmptyText));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowBusyOverlay));
    partial void OnIsListingChanged(bool value)
    {
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CanLoadMore));
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSectionChanged(StorageSection value)
    {
        OnPropertyChanged(nameof(IsBlobs));
        OnPropertyChanged(nameof(IsOverview));
    }

    partial void OnOverviewChanged(StorageAccountOverview? value)
    {
        OnPropertyChanged(nameof(IsHierarchical));
        OnPropertyChanged(nameof(NamespaceText));
        OnPropertyChanged(nameof(ShowBusyOverlay));
        BuildOverviewRows(value);
    }

    partial void OnContainerFilterTextChanged(string value) => ApplyContainerFilter();

    partial void OnSelectedContainerChanged(ContainerRow? value)
    {
        OnPropertyChanged(nameof(HasContainer));
        OnPropertyChanged(nameof(DropHint));
        DeleteContainerCommand.NotifyCanExecuteChanged();
        NotifyEntryCommands();
        if (value is null)
        {
            ClearListing();
            CurrentPrefix = string.Empty;
            return;
        }
        if (CurrentPrefix.Length == 0) _ = LoadListingAsync();
        else CurrentPrefix = string.Empty; // loads through OnCurrentPrefixChanged
    }

    partial void OnCurrentPrefixChanged(string value)
    {
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(DropHint));
        UpCommand.NotifyCanExecuteChanged();
        if (SelectedContainer is not null) _ = LoadListingAsync();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnSelectedEntryChanged(BlobEntryRow? value)
    {
        // The list's own selection drives _selection; this is the row the details pane shows.
        OnPropertyChanged(nameof(HasSelectedEntry));
        _ = LoadDetailsAsync(value);
    }

    /// <summary>Called by the view when the list's selection changes (it allows several rows).</summary>
    public void SetSelection(IEnumerable<BlobEntryRow> rows)
    {
        _selection = rows.ToList();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSingleSelection));
        OnPropertyChanged(nameof(ListingText));
        NotifyEntryCommands();
    }

    private void NotifyEntryCommands()
    {
        DownloadCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        UploadFilesCommand.NotifyCanExecuteChanged();
        UploadFolderCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        CopyUrlCommand.NotifyCanExecuteChanged();
    }

    // ---------- account and containers ----------

    private async Task LoadAccountAsync()
    {
        StorageAccountOverview? overview = null;
        await RunAsync("Reading the storage account", "Reading the storage account", async (client, ct) => overview = await client.GetOverviewAsync(ct));
        if (overview is null) return;
        Overview = overview;
        foreach (var warning in overview.Warnings) _log.Info($"[{AccountId.AccountName}] {warning}");
        await LoadContainersAsync(_startContainer);
    }

    private async Task LoadContainersAsync(string? select)
    {
        IReadOnlyList<ContainerInfo>? list = null;
        ContainersError = string.Empty;
        var ok = await RunAsync("Listing containers", "Listing containers", async (client, ct) => list = await client.ListContainersAsync(ct), inlineError: m => ContainersError = m);
        if (!ok || list is null) return;
        var keep = select ?? SelectedContainer?.Name;
        _allContainers.Clear();
        _allContainers.AddRange(list.Select(c => new ContainerRow(c)));
        ContainersLoaded = true;
        ApplyContainerFilter();
        OnPropertyChanged(nameof(ContainersEmptyText));
        var match = keep is null ? null : _allContainers.FirstOrDefault(c => c.Name == keep);
        if (match is not null && !ReferenceEquals(match, SelectedContainer))
        {
            SelectedContainer = match;
        }
        else if (match is null && SelectedContainer is not null)
        {
            SelectedContainer = null;
        }
        if (select is not null && match is null && select == _startContainer)
            StatusMessage = $"The container '{select}' saved on this entry was not found.";
        _log.Info($"[{AccountId.AccountName}] listed {list.Count} containers");
    }

    private void ApplyContainerFilter()
    {
        var selected = SelectedContainer;
        Containers.Clear();
        foreach (var c in _allContainers)
            if (ContainerFilterText.Length == 0 || c.Name.Contains(ContainerFilterText, StringComparison.OrdinalIgnoreCase))
                Containers.Add(c);
        if (selected is not null && !Containers.Contains(selected) && _allContainers.Contains(selected)) Containers.Add(selected);
    }

    [RelayCommand]
    private async Task NewContainer()
    {
        if (!ContainersLoaded) return;
        var name = _dialogs.Prompt("New container",
            "Name the new container: 3 to 63 lower case letters, digits and hyphens. It is private (no anonymous access).",
            string.Empty,
            text => BlobPaths.ValidateContainerName(text) ?? (_allContainers.Any(c => c.Name == text) ? "A container with that name already exists." : null),
            "Create");
        if (name is null) return;
        if (await RunAsync($"Creating container {name}", "Creating the container", (c, ct) => c.CreateContainerAsync(name, ct)))
        {
            _log.Info($"[{AccountId.AccountName}] created container '{name}'");
            StatusMessage = $"Created container {name}.";
            await LoadContainersAsync(name);
        }
    }

    private bool CanDeleteContainer() => SelectedContainer is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteContainer))]
    private async Task DeleteContainer()
    {
        if (SelectedContainer is not { } container) return;
        var protection = Overview?.ContainerSoftDeleteDays is { } days
            ? $"Container soft delete is on, so it can be restored in the Azure portal for {days} day{StorageJobViewModel.Plural(days)}."
            : Overview?.SoftDeleteKnown == true ? "Container soft delete is OFF: this cannot be undone." : "Assume this cannot be undone.";
        var typed = _dialogs.Prompt("Delete container",
            $"This deletes the container '{container.Name}' and EVERY blob in it. {protection}\n\nType the container name to confirm.",
            string.Empty,
            text => text == container.Name ? null : "Type the container name exactly.",
            "Delete");
        if (typed != container.Name) return;
        if (await RunAsync($"Deleting container {container.Name}", "Deleting the container", (c, ct) => c.DeleteContainerAsync(container.Name, ct)))
        {
            _log.Info($"[{AccountId.AccountName}] deleted container '{container.Name}'");
            _pendingFolders.Remove(container.Name);
            SelectedContainer = null;
            StatusMessage = $"Deleted container {container.Name}. Azure can take a while to finish; the name cannot be reused until it has.";
            await LoadContainersAsync(null);
        }
    }

    // ---------- listing and navigation ----------

    private void ClearListing()
    {
        _listCts?.Cancel();
        _allRows.Clear();
        Entries.Clear();
        _continuation = null;
        ListingError = string.Empty;
        Breadcrumbs.Clear();
        SelectedEntry = null;
        SetSelection(Array.Empty<BlobEntryRow>());
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(ListingText));
        OnPropertyChanged(nameof(EmptyText));
    }

    private async Task LoadListingAsync(bool append = false)
    {
        if (SelectedContainer is not { } container) return;
        var prefix = CurrentPrefix;
        _listCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _listCts = cts;

        if (!append)
        {
            _allRows.Clear();
            Entries.Clear();
            _continuation = null;
            SelectedEntry = null;
            SetSelection(Array.Empty<BlobEntryRow>());
            Breadcrumbs.Clear();
            Breadcrumbs.Add(new BreadcrumbItem(container.Name, string.Empty));
            foreach (var (name, p) in BlobPaths.Breadcrumb(prefix)) Breadcrumbs.Add(new BreadcrumbItem(name, p));
        }
        ListingError = string.Empty;
        IsListing = true;
        BlobListPage? page = null;
        try
        {
            page = await WithClientAsync(cts.Token, (client, ct) => client.ListFolderAsync(container.Name, prefix, append ? _continuation : null, PageSize, ct));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            ListingError = StorageErrors.Describe(ex, "Listing the folder");
            ErrorNeedsSignIn = IsSignInProblem(ex);
            if (ErrorNeedsSignIn) ErrorText = ListingError;
            _log.Warn($"[{AccountId.AccountName}] listing {container.Name}/{prefix} failed: {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
        finally
        {
            if (ReferenceEquals(_listCts, cts))
            {
                _listCts = null;
                IsListing = false;
            }
        }
        if (page is null || cts.IsCancellationRequested) return;

        _continuation = page.Continuation;
        var known = new HashSet<string>(_allRows.Select(r => r.Name), StringComparer.Ordinal);
        foreach (var entry in page.Entries)
            if (known.Add(entry.Name)) _allRows.Add(new BlobEntryRow(entry));

        // A virtual folder made here becomes real once it shows up in a listing or has something in it.
        if (_pendingFolders.TryGetValue(container.Name, out var pending))
        {
            if (prefix.Length > 0 && page.Entries.Count > 0) pending.Remove(prefix);
            foreach (var p in pending.ToList())
            {
                var name = p.TrimEnd('/');
                if (known.Contains(name)) { pending.Remove(p); continue; }
                if (BlobPaths.Parent(p) == prefix) _allRows.Insert(0, new BlobEntryRow(new BlobEntry { Name = name, IsFolder = true }, isPending: true));
            }
        }
        SortRows();
        ApplyFilter();
        OnPropertyChanged(nameof(CanLoadMore));
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    private void SortRows()
    {
        var sorted = _allRows.OrderByDescending(r => r.IsFolder).ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        _allRows.Clear();
        _allRows.AddRange(sorted);
    }

    private void ApplyFilter()
    {
        var selected = SelectedEntry;
        Entries.Clear();
        foreach (var row in _allRows)
            if (FilterText.Length == 0 || row.DisplayName.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
                Entries.Add(row);
        if (selected is not null && Entries.Contains(selected)) SelectedEntry = selected;
        OnPropertyChanged(nameof(ListingText));
        OnPropertyChanged(nameof(EmptyText));
    }

    private bool CanLoadMoreNow() => CanLoadMore;

    [RelayCommand(CanExecute = nameof(CanLoadMoreNow))]
    private Task LoadMore() => LoadListingAsync(append: true);

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up()
    {
        if (CanGoUp) CurrentPrefix = BlobPaths.Parent(CurrentPrefix);
    }

    [RelayCommand]
    private void Navigate(BreadcrumbItem? item)
    {
        if (item is null) return;
        if (item.Prefix == CurrentPrefix) _ = LoadListingAsync();
        else CurrentPrefix = item.Prefix;
    }

    /// <summary>Double click or Enter: a folder opens, a file downloads.</summary>
    [RelayCommand]
    private void Open(BlobEntryRow? row)
    {
        if (row is null) return;
        if (row.IsFolder) CurrentPrefix = row.Entry.FolderPrefix;
        else
        {
            SetSelection([row]);
            Download();
        }
    }

    // ---------- details ----------

    private async Task LoadDetailsAsync(BlobEntryRow? row)
    {
        _detailsCts?.Cancel();
        DetailRows = Array.Empty<PropertyRow>();
        MetadataRows = Array.Empty<PropertyRow>();
        DetailsText = string.Empty;
        if (row is null || SelectedContainer is not { } container) return;
        var url = Client().UrlOf(container.Name, row.IsFolder ? row.Entry.FolderPrefix : row.Name).ToString();
        if (row.IsFolder)
        {
            DetailRows =
            [
                new("Name", row.DisplayName),
                new("Path", $"{container.Name}/{row.Entry.FolderPrefix}"),
                new("Kind", IsHierarchical ? "Folder (directory)" : row.IsPending ? "Virtual folder, not saved until a file is uploaded into it" : "Virtual folder (a name prefix)"),
                new("URL", url),
            ];
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _detailsCts = cts;
        DetailsText = "Reading properties...";
        try
        {
            var d = await WithClientAsync(cts.Token, (client, ct) => client.GetDetailsAsync(container.Name, row.Name, ct));
            if (cts.IsCancellationRequested) return;
            static string When(DateTimeOffset? t) => t is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "unknown";
            var rows = new List<PropertyRow>
            {
                new("Name", BlobPaths.LeafName(d.Name)),
                new("Path", $"{container.Name}/{d.Name}"),
                new("URL", d.Url.ToString()),
                new("Size", $"{BlobPaths.FormatSize(d.Size)} ({d.Size.ToString("N0", CultureInfo.InvariantCulture)} bytes)"),
                new("Blob type", d.BlobType ?? "unknown"),
                new("Content type", d.ContentType ?? "none"),
                new("Access tier", d.AccessTier is null ? "unknown" : d.AccessTier + (d.AccessTierInferred ? " (account default)" : string.Empty)
                    + (d.ArchiveStatus is { Length: > 0 } a ? $", {a}" : string.Empty)),
                new("Last modified", When(d.LastModified)),
                new("Created", When(d.Created)),
            };
            if (d.LastAccessed is not null) rows.Add(new("Last accessed", When(d.LastAccessed)));
            if (d.ContentMd5 is not null) rows.Add(new("Content MD5", d.ContentMd5));
            if (!string.IsNullOrEmpty(d.ContentEncoding)) rows.Add(new("Content encoding", d.ContentEncoding));
            if (!string.IsNullOrEmpty(d.CacheControl)) rows.Add(new("Cache control", d.CacheControl));
            if (!string.IsNullOrEmpty(d.ContentDisposition)) rows.Add(new("Content disposition", d.ContentDisposition));
            rows.Add(new("Lease", d.LeaseState ?? "unknown"));
            if (d.VersionId is not null) rows.Add(new("Version ID", d.VersionId));
            if (d.CopyStatus is not null) rows.Add(new("Copy status", d.CopyStatus));
            if (d.TagCount > 0) rows.Add(new("Index tags", d.TagCount.ToString(CultureInfo.InvariantCulture)));
            if (d.HasLegalHold) rows.Add(new("Legal hold", "Yes"));
            rows.Add(new("Encrypted", d.ServerEncrypted ? "Yes" : "No"));
            rows.Add(new("ETag", d.ETag ?? string.Empty));
            DetailRows = rows;
            MetadataRows = d.Metadata.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Select(m => new PropertyRow(m.Key, m.Value)).ToList();
            DetailsText = d.Metadata.Count == 0 ? "No metadata." : string.Empty;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            DetailsText = StorageErrors.Describe(ex, "Reading the properties");
        }
        finally
        {
            if (ReferenceEquals(_detailsCts, cts)) _detailsCts = null;
        }
    }

    // ---------- overview ----------

    private void BuildOverviewRows(StorageAccountOverview? o)
    {
        if (o is null)
        {
            OverviewRows = ProtectionRows = Array.Empty<PropertyRow>();
            return;
        }
        static string YesNo(bool? value) => value is null ? "unknown" : value.Value ? "Yes" : "No";
        var rows = new List<PropertyRow>
        {
            new("Resource group", o.ResourceGroup),
            new("Subscription ID", o.SubscriptionId),
            new("Location", o.Location ?? "unknown"),
            new("Kind", o.Kind ?? "unknown"),
            new("Replication", o.Sku ?? "unknown"),
            new("Default access tier", o.AccessTier ?? "unknown"),
            new("Namespace", o.HierarchicalNamespace ? "Hierarchical (Data Lake Storage Gen2)" : "Flat"),
            new("Blob endpoint", o.BlobEndpoint.ToString()),
        };
        if (o.DfsEndpoint is not null) rows.Add(new("Data Lake endpoint", o.DfsEndpoint.ToString()));
        rows.Add(new("Created", o.Created is { } c ? c.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "unknown"));
        rows.Add(new("Tags", o.Tags.Count == 0 ? "none" : string.Join(", ", o.Tags.Select(t => $"{t.Key}={t.Value}"))));
        rows.Add(new("Resource ID", o.ResourceId));
        foreach (var warning in o.Warnings) rows.Add(new("Note", warning));
        OverviewRows = rows;

        ProtectionRows =
        [
            new("Blob soft delete", !o.SoftDeleteKnown ? "unknown" : o.BlobSoftDeleteDays is { } b ? $"On, {b} days" : "Off"),
            new("Container soft delete", !o.SoftDeleteKnown ? "unknown" : o.ContainerSoftDeleteDays is { } cd ? $"On, {cd} days" : "Off"),
            new("Versioning", YesNo(o.Versioning)),
            new("Public network access", o.PublicNetworkAccess ?? "unknown"),
            new("Firewall default", o.NetworkDefaultAction is null ? "unknown" : o.NetworkDefaultAction == "Deny" ? "Deny (selected networks only)" : "Allow (all networks)"),
            new("Anonymous blob access allowed", YesNo(o.AllowBlobPublicAccess)),
            new("Account key access allowed", o.AllowSharedKeyAccess is null ? "unknown (Azure's default is yes)" : YesNo(o.AllowSharedKeyAccess)),
            new("HTTPS only", YesNo(o.HttpsOnly)),
            new("Minimum TLS", o.MinTlsVersion ?? "unknown"),
            new("This app signs in with", "Entra ID only (Storage Blob Data roles). Account keys are never used."),
        ];
    }

    [RelayCommand]
    private void OpenInPortal()
    {
        if (Uri.TryCreate(AccountId.PortalUrl(_tenantId ?? _auth.HomeTenantId), UriKind.Absolute, out var uri))
        {
            WebSessionTabViewModel.OpenExternally(uri, _log);
            StatusMessage = "Opened the storage account in the Azure portal.";
        }
    }

    [RelayCommand]
    private void CopyResourceId()
    {
        if (_clipboard.CopyPlain(AccountId.ToString())) StatusMessage = "Copied the resource ID.";
    }

    [RelayCommand]
    private void CopyRow(PropertyRow? row)
    {
        if (row is not null && _clipboard.CopyPlain(row.Value)) StatusMessage = $"Copied {row.Label.ToLowerInvariant()}.";
    }

    private bool CanCopyUrl() => SelectedContainer is not null;

    [RelayCommand(CanExecute = nameof(CanCopyUrl))]
    private void CopyUrl()
    {
        if (SelectedContainer is not { } container) return;
        var row = _selection.Count == 1 ? _selection[0] : null;
        var name = row is null ? (CurrentPrefix.Length == 0 ? null : CurrentPrefix) : row.IsFolder ? row.Entry.FolderPrefix : row.Name;
        var url = Client().UrlOf(container.Name, name).ToString();
        if (_clipboard.CopyPlain(url)) StatusMessage = $"Copied {url}";
    }

    // ---------- refresh, sign in ----------

    [RelayCommand]
    private async Task Refresh()
    {
        if (IsBusy) return;
        if (Overview is null)
        {
            await LoadAccountAsync();
            return;
        }
        await LoadContainersAsync(SelectedContainer?.Name);
        if (SelectedContainer is not null) await LoadListingAsync();
        if (!HasError) StatusMessage = $"Refreshed at {DateTime.Now:HH:mm:ss}.";
    }

    [RelayCommand]
    private async Task SignIn()
    {
        if (IsBusy) return;
        await RunAsync("Signing in to Azure", "Signing in", (_, _) => Task.CompletedTask);
        if (!HasError) await Refresh();
    }

    [RelayCommand]
    private void Cancel()
    {
        _runCts?.Cancel();
        _listCts?.Cancel();
    }

    [RelayCommand]
    private void DismissError()
    {
        ErrorText = string.Empty;
        ErrorNeedsSignIn = false;
    }

    // ---------- new folder, rename ----------

    private bool CanWriteHere() => SelectedContainer is not null && ContainersLoaded;

    [RelayCommand(CanExecute = nameof(CanWriteHere))]
    private async Task NewFolder()
    {
        if (SelectedContainer is not { } container) return;
        var siblings = new HashSet<string>(_allRows.Select(r => r.DisplayName), StringComparer.Ordinal);
        var hint = IsHierarchical ? string.Empty : "\n\nThis account has no real folders: the folder exists once a file is uploaded into it.";
        var name = _dialogs.Prompt("New folder", $"Name the new folder in {Location}.{hint}", string.Empty,
            text => BlobPaths.ValidateLeafName(text) ?? (siblings.Contains(text) ? "Something with that name is already here." : null), "Create");
        if (name is null) return;
        var prefix = BlobPaths.Combine(CurrentPrefix, name) + "/";
        if (IsHierarchical)
        {
            if (!await RunAsync($"Creating folder {name}", "Creating the folder", (c, ct) => c.CreateFolderAsync(container.Name, prefix, ct))) return;
            _log.Info($"[{AccountId.AccountName}] created folder '{container.Name}/{prefix}'");
        }
        else
        {
            if (!_pendingFolders.TryGetValue(container.Name, out var set)) _pendingFolders[container.Name] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(prefix);
        }
        CurrentPrefix = prefix;
        StatusMessage = IsHierarchical ? $"Created folder {name}." : $"Folder {name} is ready. Upload a file into it to keep it.";
    }

    private bool CanRename() => SelectedContainer is not null && _selection.Count == 1;

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task Rename()
    {
        if (SelectedContainer is not { } container || _selection.Count != 1) return;
        var row = _selection[0];
        var siblings = new HashSet<string>(_allRows.Where(r => !ReferenceEquals(r, row)).Select(r => r.DisplayName), StringComparer.Ordinal);
        var note = IsHierarchical ? string.Empty
            : row.IsFolder ? "\n\nThis account has no real folders, so every blob in the folder is copied to the new name and then the originals are deleted."
            : "\n\nThis account has no rename, so the blob is copied to the new name and then the original is deleted.";
        var newLeaf = _dialogs.Prompt("Rename", $"New name for '{row.DisplayName}'.{note}", row.DisplayName,
            text => BlobPaths.ValidateLeafName(text) ?? (text == row.DisplayName ? "Enter a different name." : siblings.Contains(text) ? "Something with that name is already here." : null),
            "Rename");
        if (newLeaf is null) return;
        var parent = BlobPaths.ContainingPrefix(row.Name);
        var newName = BlobPaths.Combine(parent, newLeaf);

        if (row.IsPending)
        {
            var set = _pendingFolders[container.Name];
            set.Remove(row.Entry.FolderPrefix);
            set.Add(newName + "/");
            await LoadListingAsync();
            return;
        }

        if (IsHierarchical)
        {
            if (await RunAsync($"Renaming {row.DisplayName}", "Renaming", (c, ct) => c.RenamePathAsync(container.Name, row.Name, newName, row.IsFolder, ct)))
            {
                _log.Info($"[{AccountId.AccountName}] renamed '{container.Name}/{row.Name}' to '{newLeaf}'");
                StatusMessage = $"Renamed {row.DisplayName} to {newLeaf}.";
                await LoadListingAsync();
            }
            return;
        }

        if (!row.IsFolder)
        {
            StartJob($"Renaming {row.DisplayName} to {newLeaf}", container.Name, countsBytes: true, async (job, client, ct) =>
            {
                job.SetTotals(1, row.Entry.Size ?? 0);
                await client.CopyBlobAsync(container.Name, row.Name, newName, new Progress<long>(_ => { }), ct);
                job.AddBytes(row.Entry.Size ?? 0);
                await client.DeleteBlobAsync(container.Name, row.Name, ct);
                job.ItemDone();
                _log.Info($"[{AccountId.AccountName}] renamed '{container.Name}/{row.Name}' to '{newLeaf}' (copy and delete)");
                return $"Renamed to {newLeaf}.";
            });
            return;
        }

        var oldPrefix = row.Entry.FolderPrefix;
        var newPrefix = newName + "/";
        StartJob($"Renaming folder {row.DisplayName} to {newLeaf}", container.Name, countsBytes: true, async (job, client, ct) =>
        {
            job.SetPhase("Listing the blobs in the folder");
            var blobs = new List<BlobEntry>();
            await foreach (var b in client.ListAllAsync(container.Name, oldPrefix, ct)) blobs.Add(b);
            if (blobs.Count == 0) return "The folder was empty, nothing to rename.";
            if (blobs.FirstOrDefault(b => b.IsArchived) is { } archived)
                throw new InvalidOperationException($"'{archived.Name}' is in the Archive tier, so the folder cannot be renamed by copying. Nothing was changed.");
            if (blobs.Count > 1 && !_dialogs.Confirm(
                    $"Rename the folder by copying {blobs.Count} blobs ({BlobPaths.FormatSize(blobs.Sum(b => b.Size ?? 0))}) to '{newLeaf}' and then deleting the originals?\n\nIf any copy fails, nothing is deleted.",
                    "Rename folder"))
                throw new OperationCanceledException();
            job.SetTotals(blobs.Count, blobs.Sum(b => b.Size ?? 0));

            await Parallel.ForEachAsync(blobs, new ParallelOptions { MaxDegreeOfParallelism = TransferParallelism, CancellationToken = ct }, async (b, token) =>
            {
                try
                {
                    await client.CopyBlobAsync(container.Name, b.Name, BlobPaths.Retarget(b.Name, oldPrefix, newPrefix), null, token);
                    job.AddBytes(b.Size ?? 0);
                    job.ItemDone();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    job.ItemFailed(StorageErrors.Describe(ex, $"Copying {BlobPaths.LeafName(b.Name)}"));
                }
            });
            if (job.FailedItems > 0)
            {
                _log.Warn($"[{AccountId.AccountName}] folder rename '{container.Name}/{oldPrefix}' stopped: {job.FailedItems} copies failed, originals kept");
                return $"Nothing was deleted: {row.DisplayName} is unchanged, and the {job.DoneItems} copies already made are in {newLeaf}.";
            }

            job.SetPhase("Copied everything, deleting the originals");
            var failedDeletes = 0;
            await Parallel.ForEachAsync(blobs, new ParallelOptions { MaxDegreeOfParallelism = DeleteParallelism, CancellationToken = ct }, async (b, token) =>
            {
                try { await client.DeleteBlobAsync(container.Name, b.Name, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.Increment(ref failedDeletes); }
            });
            _log.Info($"[{AccountId.AccountName}] renamed folder '{container.Name}/{oldPrefix}' to '{newLeaf}' ({blobs.Count} blobs, {failedDeletes} originals not deleted)");
            return failedDeletes == 0
                ? $"Renamed to {newLeaf} ({blobs.Count} blobs)."
                : $"Copied to {newLeaf}, but {failedDeletes} original{StorageJobViewModel.Plural(failedDeletes)} could not be deleted and are still in {row.DisplayName}.";
        });
    }

    // ---------- delete ----------

    private bool CanDelete() => SelectedContainer is not null && _selection.Count > 0;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (SelectedContainer is not { } container || _selection.Count == 0) return;
        var rows = _selection.ToList();
        var pending = rows.Where(r => r.IsPending).ToList();
        rows = rows.Except(pending).ToList();
        if (pending.Count > 0 && _pendingFolders.TryGetValue(container.Name, out var set))
            foreach (var p in pending) set.Remove(p.Entry.FolderPrefix);
        if (rows.Count == 0)
        {
            _ = LoadListingAsync();
            return;
        }

        var files = rows.Count(r => !r.IsFolder);
        var folders = rows.Count - files;
        var what = rows.Count == 1
            ? rows[0].IsFolder ? $"the folder '{rows[0].DisplayName}' and everything in it" : $"'{rows[0].DisplayName}'"
            : string.Join(" and ", new[]
            {
                files > 0 ? $"{files} file{StorageJobViewModel.Plural(files)}" : null,
                folders > 0 ? $"{folders} folder{StorageJobViewModel.Plural(folders)} (and everything in them)" : null,
            }.Where(s => s is not null));
        var protection = Overview?.SoftDeleteSentence ?? "Assume this cannot be undone.";
        if (!_dialogs.Confirm($"Delete {what} from {Location}?\n\n{protection}", "Delete")) return;

        var hierarchical = IsHierarchical;
        StartJob(rows.Count == 1 ? $"Deleting {rows[0].DisplayName}" : $"Deleting {rows.Count} items", container.Name, countsBytes: false, async (job, client, ct) =>
        {
            // Real folders go in one call; virtual ones are every blob under the prefix.
            var blobNames = rows.Where(r => !r.IsFolder).Select(r => r.Name).ToList();
            var directories = new List<string>();
            foreach (var folder in rows.Where(r => r.IsFolder))
            {
                if (hierarchical) directories.Add(folder.Entry.FolderPrefix);
                else
                {
                    job.SetPhase($"Listing the blobs in {folder.DisplayName}");
                    await foreach (var b in client.ListAllAsync(container.Name, folder.Entry.FolderPrefix, ct)) blobNames.Add(b.Name);
                }
            }
            job.SetTotals(blobNames.Count + directories.Count, 0);
            await Parallel.ForEachAsync(blobNames.Select(n => (Name: n, IsDir: false)).Concat(directories.Select(d => (Name: d, IsDir: true))),
                new ParallelOptions { MaxDegreeOfParallelism = DeleteParallelism, CancellationToken = ct }, async (item, token) =>
                {
                    try
                    {
                        if (item.IsDir) await client.DeleteDirectoryAsync(container.Name, item.Name, token);
                        else await client.DeleteBlobAsync(container.Name, item.Name, token);
                        job.ItemDone();
                    }
                    catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                    {
                        job.ItemDone(); // already gone
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        job.ItemFailed(StorageErrors.Describe(ex, $"Deleting {BlobPaths.LeafName(item.Name)}"));
                    }
                });
            _log.Info($"[{AccountId.AccountName}] deleted {job.DoneItems} items from '{container.Name}' ({job.FailedItems} failed)");
            return $"Deleted {job.DoneItems} item{StorageJobViewModel.Plural(job.DoneItems)}.";
        });
    }

    // ---------- upload ----------

    [RelayCommand(CanExecute = nameof(CanWriteHere))]
    private void UploadFiles()
    {
        var files = _dialogs.PickOpenFiles($"Upload files to {Location}");
        if (files is { Count: > 0 }) _ = UploadPathsAsync(files);
    }

    [RelayCommand(CanExecute = nameof(CanWriteHere))]
    private void UploadFolder()
    {
        var folder = _dialogs.PickFolder($"Upload a folder to {Location}");
        if (folder is not null) _ = UploadPathsAsync([folder]);
    }

    /// <summary>Uploads dropped or picked files and folders into the folder being shown.</summary>
    public async Task UploadPathsAsync(IReadOnlyList<string> paths)
    {
        if (SelectedContainer is not { } container || paths.Count == 0) return;
        var prefix = CurrentPrefix;
        IReadOnlyList<UploadItem> plan;
        try
        {
            StatusMessage = "Looking at the files to upload...";
            plan = await Task.Run(() => BlobPaths.PlanUpload(paths, prefix));
        }
        catch (Exception ex)
        {
            ErrorText = StorageErrors.Describe(ex, "Reading the files to upload");
            return;
        }
        StatusMessage = string.Empty;
        if (plan.Count == 0)
        {
            StatusMessage = "Nothing to upload. Empty folders are skipped, as blob storage only keeps files.";
            return;
        }
        if (plan.Select(p => BlobPaths.ValidateBlobName(p.BlobName)).FirstOrDefault(p => p is not null) is { } problem)
        {
            ErrorText = $"Upload stopped before it started: {problem}";
            return;
        }
        if (plan.Sum(p => p.Size) > 50L * 1024 * 1024 * 1024 && !_dialogs.Confirm(
                $"This uploads {BlobPaths.FormatSize(plan.Sum(p => p.Size))}. That will take a long time and must keep this tab open. AzCopy is better for very large uploads. Carry on?", "Large upload"))
            return;

        var title = plan.Count == 1 ? $"Uploading {Path.GetFileName(plan[0].LocalPath)}" : $"Uploading {plan.Count} files";
        StartJob($"{title} to {container.Name}/{prefix}".TrimEnd('/'), container.Name, countsBytes: true, async (job, client, ct) =>
        {
            job.SetPhase("Checking for files that already exist");
            var existing = new ConcurrentBag<string>();
            await Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (item, token) =>
            {
                if (await client.ExistsAsync(container.Name, item.BlobName, token)) existing.Add(item.BlobName);
            });
            var overwrite = false;
            if (!existing.IsEmpty)
            {
                var sample = string.Join("\n", existing.Take(5).Select(n => "  • " + BlobPaths.LeafName(n)));
                overwrite = _dialogs.Confirm(
                    $"{existing.Count} of {plan.Count} file{StorageJobViewModel.Plural(plan.Count)} already exist{(existing.Count == 1 ? "s" : "")} in {container.Name}:\n{sample}{(existing.Count > 5 ? "\n  ..." : "")}\n\nReplace them? Yes replaces, No skips them.",
                    "Files already exist");
            }
            var skip = overwrite ? new HashSet<string>() : new HashSet<string>(existing, StringComparer.Ordinal);
            job.SetTotals(plan.Count, plan.Where(p => !skip.Contains(p.BlobName)).Sum(p => p.Size));
            foreach (var _ in skip) job.ItemSkipped();

            await Parallel.ForEachAsync(plan.Where(p => !skip.Contains(p.BlobName)), new ParallelOptions { MaxDegreeOfParallelism = TransferParallelism, CancellationToken = ct }, async (item, token) =>
            {
                long reported = 0;
                var progress = new SyncProgress(sent =>
                {
                    var delta = sent - Interlocked.Exchange(ref reported, sent);
                    if (delta > 0) job.AddBytes(delta);
                });
                try
                {
                    await client.UploadAsync(container.Name, item.BlobName, item.LocalPath, overwrite, progress, token);
                    job.AddBytes(item.Size - Interlocked.Read(ref reported));
                    job.ItemDone();
                }
                catch (StorageTargetExistsException)
                {
                    job.ItemSkipped(); // created by someone else since the check
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    job.ItemFailed(StorageErrors.Describe(ex, $"Uploading {Path.GetFileName(item.LocalPath)}"));
                }
            });
            _log.Info($"[{AccountId.AccountName}] uploaded {job.DoneItems} files to '{container.Name}/{prefix}' ({job.FailedItems} failed, {job.SkippedItems} skipped)");
            return $"Uploaded {job.DoneItems} file{StorageJobViewModel.Plural(job.DoneItems)} ({BlobPaths.FormatSize(job.TotalBytes)}).";
        });
    }

    // ---------- download ----------

    private bool CanDownload() => SelectedContainer is not null && _selection.Count > 0;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private void Download()
    {
        if (SelectedContainer is not { } container || _selection.Count == 0) return;
        var rows = _selection.Where(r => !r.IsPending).ToList();
        if (rows.Count == 0) return;
        if (rows.FirstOrDefault(r => !r.IsFolder && r.Entry.IsArchived) is { } archived && rows.Count == 1)
        {
            ErrorText = $"'{archived.DisplayName}' is in the Archive tier. Rehydrate it in the Azure portal (change its tier to Hot or Cool) before downloading.";
            return;
        }

        if (rows.Count == 1 && !rows[0].IsFolder)
        {
            var row = rows[0];
            var safe = BlobPaths.CleanSegment(row.DisplayName) ?? "download";
            var target = _dialogs.PickSaveFile($"Download {row.DisplayName}", "All files (*.*)|*.*", safe);
            if (target is null) return;
            var item = new DownloadItem(row.Name, target, row.Entry.Size ?? 0);
            RunDownload($"Downloading {row.DisplayName}", container.Name, [item], Path.GetDirectoryName(target)!);
            return;
        }

        var root = _dialogs.PickFolder($"Download {rows.Count} item{StorageJobViewModel.Plural(rows.Count)} from {Location} to");
        if (root is null) return;
        StartJob($"Downloading {rows.Count} item{StorageJobViewModel.Plural(rows.Count)} to {root}", container.Name, countsBytes: true, async (job, client, ct) =>
        {
            var items = new List<DownloadItem>();
            var unsafeNames = 0;
            var archivedCount = 0;
            foreach (var row in rows)
            {
                IEnumerable<BlobEntry> blobs;
                if (row.IsFolder)
                {
                    job.SetPhase($"Listing the blobs in {row.DisplayName}");
                    var list = new List<BlobEntry>();
                    await foreach (var b in client.ListAllAsync(container.Name, row.Entry.FolderPrefix, ct)) list.Add(b);
                    blobs = list;
                }
                else
                {
                    blobs = [row.Entry];
                }
                // Keep the folder's own name: strip only what is above the selected item.
                var strip = BlobPaths.ContainingPrefix(row.Name);
                foreach (var b in blobs)
                {
                    if (b.IsArchived) { archivedCount++; continue; }
                    if (BlobPaths.LocalPathFor(root, b.Name, strip) is { } local) items.Add(new DownloadItem(b.Name, local, b.Size ?? 0));
                    else unsafeNames++;
                }
            }
            if (unsafeNames > 0) _log.Warn($"[{AccountId.AccountName}] skipped {unsafeNames} blobs whose names cannot be a safe local path");

            var exists = items.Count(i => File.Exists(i.LocalPath));
            var overwrite = exists == 0 || _dialogs.Confirm(
                $"{exists} of {items.Count} files already exist in {root}. Replace them? Yes replaces, No skips them.", "Files already exist");
            var todo = overwrite ? items : items.Where(i => !File.Exists(i.LocalPath)).ToList();
            job.SetTotals(items.Count + unsafeNames + archivedCount, todo.Sum(i => i.Size));
            for (var i = 0; i < items.Count - todo.Count; i++) job.ItemSkipped();
            for (var i = 0; i < unsafeNames; i++) job.ItemFailed("Some blob names cannot be saved as Windows file names (for example a '..' folder) and were skipped.");
            for (var i = 0; i < archivedCount; i++) job.ItemFailed("Some blobs are in the Archive tier and were skipped. Rehydrate them in the Azure portal first.");
            await DownloadAllAsync(job, client, container.Name, todo, ct);
            _log.Info($"[{AccountId.AccountName}] downloaded {job.DoneItems} files from '{container.Name}' to {root}");
            return $"Downloaded {job.DoneItems} file{StorageJobViewModel.Plural(job.DoneItems)} to {root}.";
        });
    }

    private void RunDownload(string title, string container, IReadOnlyList<DownloadItem> items, string folder)
    {
        StartJob(title, container, countsBytes: true, async (job, client, ct) =>
        {
            job.SetTotals(items.Count, items.Sum(i => i.Size));
            await DownloadAllAsync(job, client, container, items, ct);
            _log.Info($"[{AccountId.AccountName}] downloaded {job.DoneItems} files from '{container}' to {folder}");
            return items.Count == 1 ? $"Saved {items[0].LocalPath}." : $"Downloaded {job.DoneItems} files to {folder}.";
        }, refreshListing: false);
    }

    private static async Task DownloadAllAsync(StorageJobViewModel job, BlobExplorerClient client, string container, IReadOnlyList<DownloadItem> items, CancellationToken ct)
    {
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = TransferParallelism, CancellationToken = ct }, async (item, token) =>
        {
            long reported = 0;
            var progress = new SyncProgress(got =>
            {
                var delta = got - Interlocked.Exchange(ref reported, got);
                if (delta > 0) job.AddBytes(delta);
            });
            try
            {
                await client.DownloadAsync(container, item.BlobName, item.LocalPath, progress, token);
                job.AddBytes(Math.Max(0, item.Size - Interlocked.Read(ref reported)));
                job.ItemDone();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                job.ItemFailed(StorageErrors.Describe(ex, $"Downloading {BlobPaths.LeafName(item.BlobName)}"));
            }
        });
    }

    // ---------- jobs ----------

    /// <summary>
    /// Runs a transfer in the panel. Sign in is checked first (so a prompt appears before anything starts);
    /// the work then carries on while the person keeps browsing. <paramref name="work"/> returns the summary.
    /// </summary>
    private async void StartJob(string title, string container, bool countsBytes, Func<StorageJobViewModel, BlobExplorerClient, CancellationToken, Task<string>> work,
        bool refreshListing = true)
    {
        var job = new StorageJobViewModel(title, container, countsBytes);
        Jobs.Insert(0, job);
        _progressTimer.Start();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, job.Cancellation.Token);
        var ct = linked.Token;
        var cancelled = false;
        var summary = string.Empty;
        try
        {
            job.SetPhase("Checking Azure sign in");
            await _auth.EnsureSignedInAsync(_tenantId, AzureAuth.StorageScope, ct);
            summary = await work(job, Client(), ct);
        }
        catch (OperationCanceledException)
        {
            // Cancel button, tab closing, or a "No" to a question the job asked.
            cancelled = true;
        }
        catch (Exception ex)
        {
            job.ItemFailed(StorageErrors.Describe(ex, title));
            ErrorNeedsSignIn = IsSignInProblem(ex);
            if (ErrorNeedsSignIn) ErrorText = StorageErrors.Describe(ex, title);
            _log.Warn($"[{AccountId.AccountName}] {title} failed: {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
        if (_lifetime.IsCancellationRequested) return;
        job.Refresh();
        job.Finish(cancelled, summary);
        if (job.IsDone) StatusMessage = summary;
        RefreshJobs();
        if (refreshListing && SelectedContainer?.Name == container) await LoadListingAsync();
    }

    private void RefreshJobs()
    {
        foreach (var job in Jobs) job.Refresh();
        if (!Jobs.Any(j => j.IsRunning)) _progressTimer.Stop();
        ClearFinishedJobsCommand.NotifyCanExecuteChanged();
    }

    private bool HasFinishedJobs() => Jobs.Any(j => !j.IsRunning);

    [RelayCommand(CanExecute = nameof(HasFinishedJobs))]
    private void ClearFinishedJobs()
    {
        foreach (var job in Jobs.Where(j => !j.IsRunning).ToList())
        {
            Jobs.Remove(job);
            job.Cancellation.Dispose();
        }
    }

    // ---------- plumbing ----------

    private static bool IsSignInProblem(Exception ex) =>
        AzureAuth.IsInteractionRequired(ex) || ex is Azure.RequestFailedException { Status: 401 } || ex is Azure.Identity.AuthenticationFailedException;

    /// <summary>Runs a quick data call that does not block the tab (listing, details), signing in first if needed.</summary>
    private async Task<T> WithClientAsync<T>(CancellationToken ct, Func<BlobExplorerClient, CancellationToken, Task<T>> work)
    {
        await _auth.EnsureSignedInAsync(_tenantId, AzureAuth.StorageScope, ct);
        try
        {
            return await work(Client(), ct);
        }
        catch (Exception ex) when (AzureAuth.IsInteractionRequired(ex) || ex is Azure.RequestFailedException { Status: 401 })
        {
            await _auth.EnsureSignedInAsync(_tenantId, AzureAuth.StorageScope, ct);
            return await work(Client(), ct);
        }
    }

    /// <summary>Signs in if needed, runs the work with the busy bar, and turns any failure into the error bar (or <paramref name="inlineError"/>).</summary>
    private async Task<bool> RunAsync(string busy, string action, Func<BlobExplorerClient, CancellationToken, Task> work, Action<string>? inlineError = null)
    {
        ErrorText = string.Empty;
        ErrorNeedsSignIn = false;
        StatusMessage = string.Empty;
        BusyText = "Checking Azure sign in...";
        IsBusy = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _runCts = cts;
        try
        {
            var ct = cts.Token;
            await _auth.EnsureSignedInAsync(_tenantId, AzureAuth.ArmScope, ct);
            await _auth.EnsureSignedInAsync(_tenantId, AzureAuth.StorageScope, ct);
            BusyText = busy + "...";
            try
            {
                await work(Client(), ct);
            }
            catch (Exception ex) when (AzureAuth.IsInteractionRequired(ex) || ex is Azure.RequestFailedException { Status: 401 })
            {
                // The token ran out between the check and the call. Sign in again and retry once.
                _log.Info($"[{AccountId.AccountName}] token refused during '{action}', signing in again");
                BusyText = "Your Azure sign in expired, signing in again...";
                await _auth.EnsureSignedInAsync(_tenantId, AzureAuth.StorageScope, ct);
                BusyText = busy + "...";
                await work(Client(), ct);
            }
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            ErrorText = $"{action} was cancelled.";
            ErrorNeedsSignIn = _auth.NeedsSignIn || !_auth.IsSignedIn;
            return false;
        }
        catch (Exception ex)
        {
            var message = StorageErrors.Describe(ex, action);
            ErrorNeedsSignIn = IsSignInProblem(ex) || _auth.NeedsSignIn;
            if (inlineError is not null && !ErrorNeedsSignIn) inlineError(message);
            else ErrorText = message;
            _log.Warn($"[{AccountId.AccountName}] {action} failed: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            return false;
        }
        finally
        {
            if (ReferenceEquals(_runCts, cts)) _runCts = null;
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>Rebuilt when sign in hands out a new credential (the account's endpoints are read again).</summary>
    private BlobExplorerClient Client()
    {
        var credential = _auth.Credential(_tenantId);
        if (_client is null || !ReferenceEquals(_clientCredential, credential))
        {
            var fresh = new BlobExplorerClient(AccountId, credential);
            _clientCredential = credential;
            // Endpoints come from the overview; reapply them without another round trip when they are known.
            if (_client is not null && Overview is not null) fresh.UseEndpoints(Overview);
            _client = fresh;
        }
        return _client;
    }

    private static string FirstLine(string message) => message.Split('\n')[0];

    /// <summary>Asks before closing a tab with transfers still running.</summary>
    public bool ConfirmClose()
    {
        var running = Jobs.Count(j => j.IsRunning);
        return running == 0 || _dialogs.Confirm(
            $"{running} transfer{StorageJobViewModel.Plural(running)} still running in {AccountId.AccountName}. Close the tab and cancel {(running == 1 ? "it" : "them")}?\n\nFiles already uploaded or downloaded are kept.",
            "Transfers running");
    }

    public void Dispose()
    {
        _auth.Changed -= OnAuthChanged;
        _progressTimer.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>IProgress that calls back on the reporting thread (Progress&lt;T&gt; would post every chunk to the UI thread).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
