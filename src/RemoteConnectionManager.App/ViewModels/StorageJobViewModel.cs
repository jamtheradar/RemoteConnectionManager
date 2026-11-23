using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.Storage;

namespace RemoteConnectionManager.App.ViewModels;

public enum StorageJobState
{
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>
/// One upload, download, delete or rename in the transfers panel. Workers on other threads only touch
/// the counters (Interlocked); the tab's timer calls <see cref="Refresh"/> on the UI thread to show them.
/// </summary>
public sealed partial class StorageJobViewModel : ObservableObject
{
    private long _doneBytes;
    private int _doneItems;
    private int _failedItems;
    private int _skippedItems;
    private string? _firstError;
    private string _phase = string.Empty;

    [ObservableProperty] private StorageJobState _state = StorageJobState.Running;
    [ObservableProperty] private string _progressText = "Starting...";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isIndeterminate = true;

    public StorageJobViewModel(string title, string container, bool countsBytes)
    {
        Title = title;
        Container = container;
        CountsBytes = countsBytes;
    }

    public string Title { get; }
    public string Container { get; }
    /// <summary>Uploads and downloads show bytes; deletes and renames show items.</summary>
    public bool CountsBytes { get; }
    public CancellationTokenSource Cancellation { get; } = new();
    public long TotalBytes { get; private set; }
    public int TotalItems { get; private set; }
    public int DoneItems => Volatile.Read(ref _doneItems);
    public int FailedItems => Volatile.Read(ref _failedItems);
    public int SkippedItems => Volatile.Read(ref _skippedItems);
    public string? FirstError => _firstError;

    public bool IsRunning => State == StorageJobState.Running;
    public bool IsFailed => State == StorageJobState.Failed;
    public bool IsDone => State == StorageJobState.Done;

    partial void OnStateChanged(StorageJobState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsDone));
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Shown while the size is not known yet, for example "Listing blobs".</summary>
    public void SetPhase(string phase)
    {
        _phase = phase;
        Refresh();
    }

    public void SetTotals(int items, long bytes)
    {
        TotalItems = items;
        TotalBytes = bytes;
        _phase = string.Empty;
        Refresh();
    }

    public void AddBytes(long bytes) => Interlocked.Add(ref _doneBytes, bytes);
    public void ItemDone() => Interlocked.Increment(ref _doneItems);
    public void ItemSkipped() => Interlocked.Increment(ref _skippedItems);

    public void ItemFailed(string message)
    {
        Interlocked.Increment(ref _failedItems);
        Interlocked.CompareExchange(ref _firstError, message, null);
    }

    /// <summary>Updates the text and bar from the counters. UI thread only.</summary>
    public void Refresh()
    {
        if (State != StorageJobState.Running) return;
        if (_phase.Length > 0 || TotalItems == 0)
        {
            IsIndeterminate = true;
            ProgressText = _phase.Length > 0 ? _phase + "..." : "Starting...";
            return;
        }
        IsIndeterminate = false;
        var items = DoneItems + FailedItems + SkippedItems;
        if (CountsBytes && TotalBytes > 0)
        {
            var bytes = Math.Min(Interlocked.Read(ref _doneBytes), TotalBytes);
            Percent = 100.0 * bytes / TotalBytes;
            ProgressText = $"{BlobPaths.FormatSize(bytes)} of {BlobPaths.FormatSize(TotalBytes)} · {items} of {TotalItems} file{Plural(TotalItems)}" + Problems();
        }
        else
        {
            Percent = TotalItems == 0 ? 0 : 100.0 * items / TotalItems;
            ProgressText = $"{items} of {TotalItems}" + Problems();
        }
    }

    /// <summary>Marks the job finished and writes the summary line. UI thread only.</summary>
    public void Finish(bool cancelled, string doneText)
    {
        IsIndeterminate = false;
        if (cancelled)
        {
            State = StorageJobState.Cancelled;
            ProgressText = $"Cancelled after {DoneItems} of {TotalItems}." + Problems();
        }
        else if (FailedItems > 0)
        {
            State = StorageJobState.Failed;
            Percent = 100;
            ProgressText = $"{FailedItems} failed. {FirstError}" + (doneText.Length > 0 ? " " + doneText : string.Empty);
        }
        else
        {
            State = StorageJobState.Done;
            Percent = 100;
            ProgressText = doneText + (SkippedItems > 0 ? $" {SkippedItems} skipped." : string.Empty);
        }
    }

    private string Problems() =>
        (FailedItems > 0 ? $" · {FailedItems} failed" : string.Empty) + (SkippedItems > 0 ? $" · {SkippedItems} skipped" : string.Empty);

    internal static string Plural(int n) => n == 1 ? string.Empty : "s";

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        try { Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        ProgressText = "Cancelling...";
    }
}
