using System.Globalization;

namespace RemoteConnectionManager.Storage;

/// <summary>A local file to upload and the blob name it becomes.</summary>
public sealed record UploadItem(string LocalPath, string BlobName, long Size);

/// <summary>A blob to download and the local file it becomes.</summary>
public sealed record DownloadItem(string BlobName, string LocalPath, long Size);

/// <summary>
/// Blob names and folder prefixes. Blob storage has no folders unless the account has a hierarchical
/// namespace: a "folder" is a name prefix ending in '/'. Everything here is pure so it can be tested.
/// </summary>
public static class BlobPaths
{
    public const int MaxBlobNameLength = 1024;

    /// <summary>"" for the container root, otherwise "a/b/" (no leading slash, one trailing slash).</summary>
    public static string NormalisePrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return string.Empty;
        var trimmed = prefix.Trim('/');
        return trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    /// <summary>The prefix one level up: "a/" for "a/b/", "" for "a/" or "".</summary>
    public static string Parent(string prefix)
    {
        var p = NormalisePrefix(prefix).TrimEnd('/');
        var slash = p.LastIndexOf('/');
        return slash < 0 ? string.Empty : p[..(slash + 1)];
    }

    /// <summary>The prefix that holds a blob or folder: "a/b/" for "a/b/c.txt" or "a/b/c/".</summary>
    public static string ContainingPrefix(string name) => Parent(name.TrimEnd('/') + "/");

    /// <summary>The last segment: "c.txt" for "a/b/c.txt", "b" for "a/b/".</summary>
    public static string LeafName(string name)
    {
        var trimmed = name.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    public static string Combine(string prefix, string leaf) => NormalisePrefix(prefix) + leaf;

    /// <summary>Each folder from the root down, for the breadcrumb: ("a", "a/"), ("b", "a/b/").</summary>
    public static IReadOnlyList<(string Name, string Prefix)> Breadcrumb(string prefix)
    {
        var list = new List<(string, string)>();
        var acc = string.Empty;
        foreach (var segment in NormalisePrefix(prefix).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc += segment + "/";
            list.Add((segment, acc));
        }
        return list;
    }

    /// <summary>
    /// Why a new container name is not allowed, or null when it is: 3 to 63 lower case letters, digits
    /// and hyphens, starting and ending with a letter or digit, with no two hyphens together.
    /// </summary>
    public static string? ValidateContainerName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "Enter a container name.";
        if (name.Length is < 3 or > 63) return "A container name must be 3 to 63 characters long.";
        if (name.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
            return "A container name can only use lower case letters, digits and hyphens.";
        if (name[0] == '-' || name[^1] == '-') return "A container name must start and end with a letter or digit.";
        if (name.Contains("--", StringComparison.Ordinal)) return "A container name cannot have two hyphens in a row.";
        return null;
    }

    /// <summary>
    /// Why a single file or folder name (no slashes) is not allowed, or null when it is. Blob storage
    /// accepts almost anything, so this only stops names that break folders or other tools.
    /// </summary>
    public static string? ValidateLeafName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name.Contains('/') || name.Contains('\\')) return "A name cannot contain / or \\. Create one folder at a time.";
        if (name is "." or "..") return "A name cannot be . or ..";
        if (name.EndsWith('.')) return "A name cannot end with a dot.";
        if (name.Any(char.IsControl)) return "A name cannot contain control characters.";
        if (name != name.Trim()) return "A name cannot start or end with a space.";
        return null;
    }

    /// <summary>Why a full blob name is not allowed, or null.</summary>
    public static string? ValidateBlobName(string name)
    {
        if (name.Length == 0) return "A blob name cannot be empty.";
        if (name.Length > MaxBlobNameLength) return $"'{Shorten(name)}' is longer than {MaxBlobNameLength} characters.";
        if (name.EndsWith('/') || name.EndsWith('.')) return $"'{Shorten(name)}' cannot end with / or a dot.";
        if (name.Split('/').Any(s => s.Length == 0)) return $"'{Shorten(name)}' has an empty folder name (two slashes together).";
        return null;
    }

    /// <summary>The new name of something under <paramref name="oldPrefix"/> when that folder becomes <paramref name="newPrefix"/>.</summary>
    public static string Retarget(string name, string oldPrefix, string newPrefix)
    {
        oldPrefix = NormalisePrefix(oldPrefix);
        newPrefix = NormalisePrefix(newPrefix);
        if (!name.StartsWith(oldPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"'{name}' is not under '{oldPrefix}'.", nameof(name));
        return newPrefix + name[oldPrefix.Length..];
    }

    /// <summary>
    /// Folders and blobs for one level of a listing. With a hierarchical namespace a folder also shows up
    /// as a zero length blob with hdi_isfolder=true (and, when empty, only that way), so those become folders
    /// and duplicates are dropped. Folders first, then blobs, each in name order.
    /// </summary>
    public static IReadOnlyList<BlobEntry> BuildListing(IEnumerable<string> prefixes, IEnumerable<(BlobEntry Blob, bool IsFolderMarker)> blobs)
    {
        var folders = new Dictionary<string, BlobEntry>(StringComparer.Ordinal);
        var files = new List<BlobEntry>();
        foreach (var prefix in prefixes)
        {
            var name = prefix.TrimEnd('/');
            if (name.Length > 0) folders.TryAdd(name, new BlobEntry { Name = name, IsFolder = true });
        }
        foreach (var (blob, marker) in blobs)
        {
            if (marker)
            {
                var name = blob.Name.TrimEnd('/');
                folders[name] = new BlobEntry { Name = name, IsFolder = true, LastModified = blob.LastModified };
            }
            else
            {
                files.Add(blob);
            }
        }
        return folders.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// The blobs to create for dropped or picked files and folders. A file lands in <paramref name="prefix"/>
    /// under its own name; a folder lands as prefix + folder name, keeping its tree. Hidden and system files
    /// inside folders are skipped, as Explorer would not show them either.
    /// </summary>
    public static IReadOnlyList<UploadItem> PlanUpload(IEnumerable<string> localPaths, string prefix)
    {
        prefix = NormalisePrefix(prefix);
        var items = new List<UploadItem>();
        foreach (var path in localPaths)
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                items.Add(new UploadItem(info.FullName, prefix + info.Name, info.Length));
            }
            else if (Directory.Exists(path))
            {
                var root = new DirectoryInfo(path);
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                };
                foreach (var file in root.EnumerateFiles("*", options))
                {
                    var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
                    items.Add(new UploadItem(file.FullName, prefix + root.Name + "/" + relative, file.Length));
                }
            }
        }
        return items;
    }

    /// <summary>
    /// Where a blob lands when <paramref name="stripPrefix"/> is downloaded into <paramref name="localRoot"/>.
    /// Blob names may hold characters Windows does not allow, and even ".." segments, so each segment is
    /// cleaned and the result is checked to stay inside the root. Null when the name cannot be placed safely.
    /// </summary>
    public static string? LocalPathFor(string localRoot, string blobName, string stripPrefix)
    {
        stripPrefix = NormalisePrefix(stripPrefix);
        if (!blobName.StartsWith(stripPrefix, StringComparison.Ordinal)) return null;
        var relative = blobName[stripPrefix.Length..];
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(CleanSegment).ToList();
        if (segments.Count == 0 || segments.Any(s => s is null)) return null;

        var root = Path.GetFullPath(localRoot);
        var full = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments.Select(s => s!)).ToArray()));
        var rootWithSlash = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A blob name segment made safe as a Windows file or folder name. Null for "." and "..".</summary>
    public static string? CleanSegment(string segment)
    {
        if (segment is "." or "..") return null;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = segment.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).TrimEnd('.', ' ');
        if (cleaned.Length == 0) cleaned = "_";
        var stem = cleaned.Split('.')[0];
        if (ReservedNames.Contains(stem)) cleaned = "_" + cleaned;
        return cleaned;
    }

    /// <summary>"0 B", "1.5 KB", "23.4 MB" (1024 based, as Explorer shows sizes).</summary>
    public static string FormatSize(long? bytes)
    {
        if (bytes is not { } b) return string.Empty;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = b;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{b} B"
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain", [".log"] = "text/plain", [".csv"] = "text/csv", [".md"] = "text/markdown",
        [".htm"] = "text/html", [".html"] = "text/html", [".css"] = "text/css", [".js"] = "text/javascript", [".mjs"] = "text/javascript",
        [".json"] = "application/json", [".xml"] = "application/xml", [".yaml"] = "application/yaml", [".yml"] = "application/yaml",
        [".pdf"] = "application/pdf", [".zip"] = "application/zip", [".gz"] = "application/gzip", [".7z"] = "application/x-7z-compressed",
        [".tar"] = "application/x-tar", [".wasm"] = "application/wasm",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".svg"] = "image/svg+xml",
        [".webp"] = "image/webp", [".ico"] = "image/x-icon", [".bmp"] = "image/bmp",
        [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".bak"] = "application/octet-stream", [".bacpac"] = "application/octet-stream", [".vhd"] = "application/octet-stream",
    };

    /// <summary>The Content-Type to upload a file with, so browsers and static websites serve it correctly.</summary>
    public static string ContentTypeFor(string fileName) =>
        ContentTypes.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";

    private static string Shorten(string name) => name.Length > 80 ? name[..77] + "..." : name;
}
