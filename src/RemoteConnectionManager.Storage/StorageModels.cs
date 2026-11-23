namespace RemoteConnectionManager.Storage;

/// <summary>A storage account found while listing a subscription.</summary>
public sealed record StorageAccountInfo(
    string Name,
    string ResourceId,
    string SubscriptionId,
    string SubscriptionName,
    string ResourceGroup,
    string Location,
    string Kind,
    string? Sku,
    bool HierarchicalNamespace,
    string? TenantId);

/// <summary>What the tab shows about the account, and the endpoints it talks to.</summary>
public sealed record StorageAccountOverview
{
    public required string Name { get; init; }
    public required string ResourceId { get; init; }
    public required string ResourceGroup { get; init; }
    public required string SubscriptionId { get; init; }
    public required Uri BlobEndpoint { get; init; }
    public Uri? DfsEndpoint { get; init; }
    /// <summary>False when the account could not be read through Azure Resource Manager (only blob data roles).</summary>
    public bool FromResourceManager { get; init; }
    public string? Location { get; init; }
    public string? Kind { get; init; }
    public string? Sku { get; init; }
    public string? AccessTier { get; init; }
    /// <summary>Hierarchical namespace (Data Lake Storage Gen2): folders are real and rename is one call.</summary>
    public bool HierarchicalNamespace { get; init; }
    public string? PublicNetworkAccess { get; init; }
    public string? NetworkDefaultAction { get; init; }
    public bool? AllowBlobPublicAccess { get; init; }
    public bool? AllowSharedKeyAccess { get; init; }
    public bool? HttpsOnly { get; init; }
    public string? MinTlsVersion { get; init; }
    public DateTimeOffset? Created { get; init; }
    /// <summary>Blob soft delete retention in days; null when off or not known (see <see cref="SoftDeleteKnown"/>).</summary>
    public int? BlobSoftDeleteDays { get; init; }
    public int? ContainerSoftDeleteDays { get; init; }
    public bool? Versioning { get; init; }
    public bool SoftDeleteKnown { get; init; }
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>One line for delete confirmations: whether a deleted blob can be got back.</summary>
    public string SoftDeleteSentence => !SoftDeleteKnown
        ? "It is not known whether soft delete is on for this account, so assume this cannot be undone."
        : BlobSoftDeleteDays is { } days
            ? $"Soft delete is on: deleted blobs can be restored in the Azure portal for {days} day{(days == 1 ? "" : "s")}."
            : "Soft delete is OFF for this account: this cannot be undone.";
}

/// <summary>A blob container.</summary>
public sealed record ContainerInfo(string Name, DateTimeOffset? LastModified, string PublicAccess, string? LeaseState, bool HasImmutability, bool HasLegalHold);

/// <summary>A row in a folder listing: a folder (virtual, or real with a hierarchical namespace) or a blob.</summary>
public sealed record BlobEntry
{
    /// <summary>The full blob name, or for a folder its prefix without the trailing slash.</summary>
    public required string Name { get; init; }
    public bool IsFolder { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string? AccessTier { get; init; }
    public bool AccessTierInferred { get; init; }
    public string? BlobType { get; init; }
    public string? ContentType { get; init; }

    public string DisplayName => BlobPaths.LeafName(Name);
    /// <summary>The prefix that lists this folder's contents ("a/b/").</summary>
    public string FolderPrefix => IsFolder ? Name.TrimEnd('/') + "/" : throw new InvalidOperationException("Not a folder.");
    public bool IsArchived => string.Equals(AccessTier, "Archive", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One page of a folder listing. <see cref="Continuation"/> is null when there is no more.</summary>
public sealed record BlobListPage(IReadOnlyList<BlobEntry> Entries, string? Continuation);

/// <summary>A blob's properties and metadata, as the details pane shows them.</summary>
public sealed record BlobDetails
{
    public required string Name { get; init; }
    public required Uri Url { get; init; }
    public string? BlobType { get; init; }
    public long Size { get; init; }
    public string? ContentType { get; init; }
    public string? ContentMd5 { get; init; }
    public string? ContentEncoding { get; init; }
    public string? CacheControl { get; init; }
    public string? ContentDisposition { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public DateTimeOffset? LastAccessed { get; init; }
    public string? ETag { get; init; }
    public string? AccessTier { get; init; }
    public bool AccessTierInferred { get; init; }
    public string? ArchiveStatus { get; init; }
    public string? LeaseState { get; init; }
    public bool ServerEncrypted { get; init; }
    public string? VersionId { get; init; }
    public string? CopyStatus { get; init; }
    public long TagCount { get; init; }
    public bool HasLegalHold { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Thrown when a rename or upload would replace something that is already there.</summary>
public sealed class StorageTargetExistsException(string name) : Exception($"'{name}' already exists.")
{
    public string TargetName { get; } = name;
}
