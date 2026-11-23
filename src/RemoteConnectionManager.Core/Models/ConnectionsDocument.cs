using System.Text.Json.Serialization;

namespace RemoteConnectionManager.Core.Models;

/// <summary>The whole connections.json file. Root level folders and loose connections.</summary>
public sealed class ConnectionsDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public ConnectionDefaults Defaults { get; set; } = new();
    public List<ConnectionFolder> Folders { get; set; } = new();
    public List<ConnectionEntry> Connections { get; set; } = new();

    /// <summary>Every entry in the document with the folder chain above it, outermost first.</summary>
    public IEnumerable<(ConnectionEntry Entry, IReadOnlyList<ConnectionFolder> Chain)> AllEntries()
    {
        foreach (var entry in Connections)
            yield return (entry, Array.Empty<ConnectionFolder>());

        foreach (var folder in Folders)
            foreach (var item in Walk(folder, new List<ConnectionFolder>()))
                yield return item;
    }

    public IEnumerable<ConnectionFolder> AllFolders()
    {
        foreach (var folder in Folders)
        {
            yield return folder;
            foreach (var child in Descendants(folder))
                yield return child;
        }
    }

    public ConnectionEntry? FindEntry(Guid id) => AllEntries().Select(e => e.Entry).FirstOrDefault(e => e.Id == id);

    public ConnectionFolder? FindFolder(Guid id) => AllFolders().FirstOrDefault(f => f.Id == id);

    /// <summary>Folder that directly contains the entry, or null when it sits at the root.</summary>
    public ConnectionFolder? ParentOf(ConnectionEntry entry) =>
        AllFolders().FirstOrDefault(f => f.Connections.Contains(entry));

    public ConnectionFolder? ParentOf(ConnectionFolder folder) =>
        AllFolders().FirstOrDefault(f => f.Folders.Contains(folder));

    public IReadOnlyList<ConnectionFolder> ChainFor(ConnectionEntry entry) =>
        AllEntries().FirstOrDefault(e => ReferenceEquals(e.Entry, entry)).Chain ?? Array.Empty<ConnectionFolder>();

    public bool Remove(ConnectionEntry entry)
    {
        if (Connections.Remove(entry)) return true;
        var parent = ParentOf(entry);
        return parent is not null && parent.Connections.Remove(entry);
    }

    public bool Remove(ConnectionFolder folder)
    {
        if (Folders.Remove(folder)) return true;
        var parent = ParentOf(folder);
        return parent is not null && parent.Folders.Remove(folder);
    }

    /// <summary>The folder an entry would go into (null is the top level) is not where it already is.</summary>
    public bool CanMove(ConnectionEntry entry, ConnectionFolder? target) =>
        !ReferenceEquals(ParentOf(entry), target) && (target is null || AllFolders().Contains(target));

    /// <summary>
    /// A folder can go anywhere except where it already is, into itself or into a folder inside it.
    /// </summary>
    public bool CanMove(ConnectionFolder folder, ConnectionFolder? target)
    {
        if (ReferenceEquals(ParentOf(folder), target)) return false;
        if (target is null) return true;
        if (ReferenceEquals(folder, target) || Descendants(folder).Contains(target)) return false;
        return AllFolders().Contains(target);
    }

    /// <summary>Moves an entry to the end of another folder (null is the top level). False when it cannot go there.</summary>
    public bool Move(ConnectionEntry entry, ConnectionFolder? target)
    {
        if (!CanMove(entry, target) || !Remove(entry)) return false;
        (target?.Connections ?? Connections).Add(entry);
        return true;
    }

    /// <summary>Moves a folder and everything in it to the end of another folder (null is the top level). False when it cannot go there.</summary>
    public bool Move(ConnectionFolder folder, ConnectionFolder? target)
    {
        if (!CanMove(folder, target) || !Remove(folder)) return false;
        (target?.Folders ?? Folders).Add(folder);
        return true;
    }

    [JsonIgnore]
    public int EntryCount => AllEntries().Count();

    private static IEnumerable<(ConnectionEntry, IReadOnlyList<ConnectionFolder>)> Walk(ConnectionFolder folder, List<ConnectionFolder> chain)
    {
        chain.Add(folder);
        var snapshot = chain.ToArray();
        foreach (var entry in folder.Connections)
            yield return (entry, snapshot);
        foreach (var child in folder.Folders)
            foreach (var item in Walk(child, chain))
                yield return item;
        chain.RemoveAt(chain.Count - 1);
    }

    private static IEnumerable<ConnectionFolder> Descendants(ConnectionFolder folder)
    {
        foreach (var child in folder.Folders)
        {
            yield return child;
            foreach (var grandChild in Descendants(child))
                yield return grandChild;
        }
    }
}
