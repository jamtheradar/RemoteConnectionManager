using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Storage;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using RemoteConnectionManager.KeyVault;

namespace RemoteConnectionManager.Storage;

/// <summary>
/// Browses and changes the blobs in one storage account, signed in with Entra ID only. Azure IAM
/// decides what is allowed: Storage Blob Data Reader to list and download, Contributor to write.
/// Account settings (endpoints, soft delete) come from Azure Resource Manager when the account can
/// read them; without that the public cloud endpoint is assumed.
/// </summary>
public sealed class BlobExplorerClient
{
    /// <summary>Put Blob From URL copies up to this size in one call; larger blobs go block by block.</summary>
    private const long SingleCopyLimit = 256L * 1024 * 1024;
    private const long CopyBlockSize = 100L * 1024 * 1024;
    private const string FolderMarker = "hdi_isfolder";

    private readonly TokenCredential _credential;
    private readonly ArmClient _arm;
    private BlobServiceClient _blobs;
    private DataLakeServiceClient? _lake;

    public BlobExplorerClient(StorageAccountId id, TokenCredential credential)
    {
        Id = id;
        _credential = credential;
        _arm = new ArmClient(credential);
        _blobs = new BlobServiceClient(id.DefaultBlobEndpoint, credential, Options());
    }

    public StorageAccountId Id { get; }
    public Uri BlobEndpoint => _blobs.Uri;
    /// <summary>Known after <see cref="GetOverviewAsync"/>.</summary>
    public bool HierarchicalNamespace { get; private set; }

    private static BlobClientOptions Options()
    {
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 4;
        options.Retry.NetworkTimeout = TimeSpan.FromMinutes(5);
        return options;
    }

    // ---------- account ----------

    /// <summary>Reads the account through Resource Manager and points the data clients at its own endpoints.</summary>
    public async Task<StorageAccountOverview> GetOverviewAsync(CancellationToken ct)
    {
        var warnings = new List<string>();
        StorageAccountData? data = null;
        StorageAccountResource? account = null;
        try
        {
            account = (await _arm.GetStorageAccountResource(new ResourceIdentifier(Id.ToString())).GetAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
            data = account.Data;
        }
        catch (RequestFailedException ex) when (ex.Status is 403 or 404)
        {
            warnings.Add(ex.Status == 403
                ? "No access to read the account's settings in Azure (needs Reader on the account). Using the default endpoint."
                : "The account was not found through Azure Resource Manager. Using the default endpoint.");
        }

        var blobUri = data?.PrimaryEndpoints?.BlobUri ?? Id.DefaultBlobEndpoint;
        var dfsUri = data?.PrimaryEndpoints?.DfsUri;
        if (blobUri != _blobs.Uri) _blobs = new BlobServiceClient(blobUri, _credential, Options());

        var hns = data?.IsHnsEnabled == true;
        if (data is null)
        {
            try
            {
                var info = (await _blobs.GetAccountInfoAsync(ct).ConfigureAwait(false)).Value;
                hns = info.IsHierarchicalNamespaceEnabled;
            }
            catch (RequestFailedException)
            {
                warnings.Add("Could not tell whether the account has a hierarchical namespace; treating folders as virtual.");
            }
        }
        SetNamespace(hns, blobUri, dfsUri);

        int? blobDays = null, containerDays = null;
        bool? versioning = null;
        var softDeleteKnown = false;
        if (account is not null)
        {
            try
            {
                var service = (await account.GetBlobService().GetAsync(ct).ConfigureAwait(false)).Value.Data;
                softDeleteKnown = true;
                if (service.DeleteRetentionPolicy is { IsEnabled: true } policy) blobDays = policy.Days ?? 7;
                if (service.ContainerDeleteRetentionPolicy is { IsEnabled: true } cpolicy) containerDays = cpolicy.Days ?? 7;
                versioning = service.IsVersioningEnabled;
            }
            catch (RequestFailedException ex)
            {
                warnings.Add($"Blob service settings not read ({ex.Status}), so soft delete is unknown.");
            }
        }

        return new StorageAccountOverview
        {
            Name = Id.AccountName,
            ResourceId = Id.ToString(),
            ResourceGroup = Id.ResourceGroup,
            SubscriptionId = Id.SubscriptionId,
            BlobEndpoint = blobUri,
            DfsEndpoint = dfsUri,
            FromResourceManager = data is not null,
            Location = data is null ? null : data.Location.DisplayName ?? data.Location.ToString(),
            Kind = data?.Kind?.ToString(),
            Sku = data?.Sku?.Name.ToString(),
            AccessTier = data?.AccessTier?.ToString(),
            HierarchicalNamespace = hns,
            PublicNetworkAccess = data?.PublicNetworkAccess?.ToString(),
            NetworkDefaultAction = data?.NetworkRuleSet?.DefaultAction.ToString(),
            AllowBlobPublicAccess = data?.AllowBlobPublicAccess,
            AllowSharedKeyAccess = data?.AllowSharedKeyAccess,
            HttpsOnly = data?.EnableHttpsTrafficOnly,
            MinTlsVersion = data?.MinimumTlsVersion?.ToString(),
            Created = data?.CreatedOn,
            BlobSoftDeleteDays = blobDays,
            ContainerSoftDeleteDays = containerDays,
            Versioning = versioning,
            SoftDeleteKnown = softDeleteKnown,
            Tags = data is null ? new Dictionary<string, string>() : new Dictionary<string, string>(data.Tags),
            Warnings = warnings,
        };
    }

    /// <summary>Points a fresh client (after sign in changed) at endpoints already read, without another call.</summary>
    public void UseEndpoints(StorageAccountOverview overview)
    {
        if (overview.BlobEndpoint != _blobs.Uri) _blobs = new BlobServiceClient(overview.BlobEndpoint, _credential, Options());
        SetNamespace(overview.HierarchicalNamespace, overview.BlobEndpoint, overview.DfsEndpoint);
    }

    private void SetNamespace(bool hns, Uri blobUri, Uri? dfsUri)
    {
        HierarchicalNamespace = hns;
        _lake = hns
            ? new DataLakeServiceClient(dfsUri ?? new Uri(blobUri.ToString().Replace(".blob.", ".dfs.", StringComparison.OrdinalIgnoreCase)), _credential)
            : null;
    }

    // ---------- containers ----------

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct)
    {
        var list = new List<ContainerInfo>();
        await foreach (var c in _blobs.GetBlobContainersAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            var p = c.Properties;
            list.Add(new ContainerInfo(c.Name, p.LastModified, p.PublicAccess?.ToString() ?? "None", p.LeaseState?.ToString(),
                p.HasImmutabilityPolicy == true, p.HasLegalHold == true));
        }
        return list.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    public async Task CreateContainerAsync(string name, CancellationToken ct)
    {
        if (BlobPaths.ValidateContainerName(name) is { } problem) throw new InvalidOperationException(problem);
        await _blobs.GetBlobContainerClient(name).CreateAsync(Azure.Storage.Blobs.Models.PublicAccessType.None, cancellationToken: ct).ConfigureAwait(false);
    }

    public Task DeleteContainerAsync(string name, CancellationToken ct) =>
        _blobs.GetBlobContainerClient(name).DeleteAsync(cancellationToken: ct);

    // ---------- listing ----------

    /// <summary>One level of a folder: sub folders and blobs, a page at a time.</summary>
    public async Task<BlobListPage> ListFolderAsync(string container, string prefix, string? continuation, int pageSize, CancellationToken ct)
    {
        prefix = BlobPaths.NormalisePrefix(prefix);
        var client = _blobs.GetBlobContainerClient(container);
        var prefixes = new List<string>();
        var blobs = new List<(BlobEntry, bool)>();
        string? next = null;
        var pages = client.GetBlobsByHierarchyAsync(BlobTraits.Metadata, BlobStates.None, "/", prefix.Length == 0 ? null : prefix, ct)
            .AsPages(continuation, pageSize);
        await foreach (var page in pages.ConfigureAwait(false))
        {
            foreach (var item in page.Values)
            {
                if (item.IsPrefix) prefixes.Add(item.Prefix);
                else blobs.Add((ToEntry(item.Blob), IsFolderMarker(item.Blob)));
            }
            next = string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken;
            break;
        }
        return new BlobListPage(BlobPaths.BuildListing(prefixes, blobs), next);
    }

    /// <summary>Every blob under a prefix, all levels (folder markers left out).</summary>
    public async IAsyncEnumerable<BlobEntry> ListAllAsync(string container, string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        prefix = BlobPaths.NormalisePrefix(prefix);
        var client = _blobs.GetBlobContainerClient(container);
        await foreach (var blob in client.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix.Length == 0 ? null : prefix, ct).ConfigureAwait(false))
        {
            if (IsFolderMarker(blob)) continue;
            yield return ToEntry(blob);
        }
    }

    private static bool IsFolderMarker(BlobItem blob) =>
        blob.Metadata is { } m && m.TryGetValue(FolderMarker, out var v) && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    private static BlobEntry ToEntry(BlobItem blob) => new()
    {
        Name = blob.Name,
        Size = blob.Properties.ContentLength,
        LastModified = blob.Properties.LastModified,
        AccessTier = blob.Properties.AccessTier?.ToString(),
        AccessTierInferred = blob.Properties.AccessTierInferred,
        BlobType = blob.Properties.BlobType?.ToString(),
        ContentType = blob.Properties.ContentType,
    };

    public Uri UrlOf(string container, string? name) =>
        string.IsNullOrEmpty(name) ? _blobs.GetBlobContainerClient(container).Uri : _blobs.GetBlobContainerClient(container).GetBlobClient(name).Uri;

    public async Task<BlobDetails> GetDetailsAsync(string container, string name, CancellationToken ct)
    {
        var blob = _blobs.GetBlobContainerClient(container).GetBlobClient(name);
        var p = (await blob.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
        return new BlobDetails
        {
            Name = name,
            Url = blob.Uri,
            BlobType = p.BlobType.ToString(),
            Size = p.ContentLength,
            ContentType = p.ContentType,
            ContentMd5 = p.ContentHash is { Length: > 0 } hash ? Convert.ToBase64String(hash) : null,
            ContentEncoding = p.ContentEncoding,
            CacheControl = p.CacheControl,
            ContentDisposition = p.ContentDisposition,
            Created = p.CreatedOn,
            LastModified = p.LastModified,
            LastAccessed = p.LastAccessed == default ? null : p.LastAccessed,
            ETag = p.ETag.ToString(),
            AccessTier = p.AccessTier,
            AccessTierInferred = p.AccessTierInferred,
            ArchiveStatus = p.ArchiveStatus,
            LeaseState = p.LeaseState.ToString(),
            ServerEncrypted = p.IsServerEncrypted,
            VersionId = p.VersionId,
            CopyStatus = string.IsNullOrEmpty(p.CopyId) ? null : p.CopyStatus.ToString(),
            TagCount = p.TagCount,
            HasLegalHold = p.HasLegalHold,
            Metadata = new Dictionary<string, string>(p.Metadata),
        };
    }

    public async Task<bool> ExistsAsync(string container, string name, CancellationToken ct) =>
        (await _blobs.GetBlobContainerClient(container).GetBlobClient(name).ExistsAsync(ct).ConfigureAwait(false)).Value;

    // ---------- transfers ----------

    private static StorageTransferOptions TransferOptions => new()
    {
        MaximumConcurrency = 4,
        InitialTransferSize = 8 * 1024 * 1024,
        MaximumTransferSize = 8 * 1024 * 1024,
    };

    /// <summary>
    /// Uploads one file. Without <paramref name="overwrite"/> the upload only succeeds if nothing has that
    /// name yet (so a file created meanwhile is never replaced). <paramref name="progress"/> gets bytes sent so far.
    /// </summary>
    public async Task UploadAsync(string container, string blobName, string localPath, bool overwrite, IProgress<long>? progress, CancellationToken ct)
    {
        if (BlobPaths.ValidateBlobName(blobName) is { } problem) throw new InvalidOperationException(problem);
        var blob = _blobs.GetBlobContainerClient(container).GetBlobClient(blobName);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = BlobPaths.ContentTypeFor(localPath) },
            ProgressHandler = progress,
            TransferOptions = TransferOptions,
            Conditions = overwrite ? null : new BlobRequestConditions { IfNoneMatch = ETag.All },
        };
        try
        {
            await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await blob.UploadAsync(stream, options, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (!overwrite && (ex.Status == 412 || ex.ErrorCode == "BlobAlreadyExists"))
        {
            throw new StorageTargetExistsException(blobName);
        }
    }

    /// <summary>Downloads one blob to a part file first, so a cancelled or failed download leaves nothing half written.</summary>
    public async Task DownloadAsync(string container, string blobName, string localPath, IProgress<long>? progress, CancellationToken ct)
    {
        var blob = _blobs.GetBlobContainerClient(container).GetBlobClient(blobName);
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var part = localPath + ".rcmpart";
        try
        {
            await blob.DownloadToAsync(part, new BlobDownloadToOptions { ProgressHandler = progress, TransferOptions = TransferOptions }, ct).ConfigureAwait(false);
            File.Move(part, localPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    // ---------- folders, delete and rename ----------

    /// <summary>Creates a real folder. Only for a hierarchical namespace: elsewhere folders exist once a blob is in them.</summary>
    public async Task CreateFolderAsync(string container, string folderName, CancellationToken ct)
    {
        if (_lake is null) throw new InvalidOperationException("Folders are virtual on this account; they appear once a file is uploaded into them.");
        var dir = _lake.GetFileSystemClient(container).GetDirectoryClient(folderName.TrimEnd('/'));
        try
        {
            await dir.CreateAsync(new DataLakePathCreateOptions { Conditions = new DataLakeRequestConditions { IfNoneMatch = ETag.All } }, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            throw new StorageTargetExistsException(BlobPaths.LeafName(folderName));
        }
    }

    /// <summary>Deletes one blob and its snapshots.</summary>
    public async Task DeleteBlobAsync(string container, string name, CancellationToken ct)
    {
        await _blobs.GetBlobContainerClient(container).GetBlobClient(name)
            .DeleteAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a real folder and everything in it in one call (hierarchical namespace only).</summary>
    public async Task DeleteDirectoryAsync(string container, string folderName, CancellationToken ct)
    {
        if (_lake is null) throw new InvalidOperationException("Not a hierarchical namespace account.");
        await _lake.GetFileSystemClient(container).GetDirectoryClient(folderName.TrimEnd('/')).DeleteAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames a file or folder in one call (hierarchical namespace only). Refuses when the new name is
    /// already taken instead of replacing it.
    /// </summary>
    public async Task RenamePathAsync(string container, string oldName, string newName, bool isFolder, CancellationToken ct)
    {
        if (_lake is null) throw new InvalidOperationException("Not a hierarchical namespace account.");
        var fs = _lake.GetFileSystemClient(container);
        var noReplace = new DataLakeRequestConditions { IfNoneMatch = ETag.All };
        try
        {
            if (isFolder)
                await fs.GetDirectoryClient(oldName.TrimEnd('/')).RenameAsync(newName.TrimEnd('/'), destinationConditions: noReplace, cancellationToken: ct).ConfigureAwait(false);
            else
                await fs.GetFileClient(oldName).RenameAsync(newName, destinationConditions: noReplace, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 412 || ex.ErrorCode is "PathAlreadyExists")
        {
            throw new StorageTargetExistsException(BlobPaths.LeafName(newName));
        }
    }

    /// <summary>
    /// Copies one block blob to a new name in the same container, server side, keeping its content
    /// headers, metadata and explicit access tier. Index tags are not copied. Refuses when the new name
    /// is taken. Used for rename on accounts without a hierarchical namespace (copy, then delete).
    /// </summary>
    public async Task CopyBlobAsync(string container, string sourceName, string targetName, IProgress<long>? progress, CancellationToken ct)
    {
        if (BlobPaths.ValidateBlobName(targetName) is { } problem) throw new InvalidOperationException(problem);
        var containerClient = _blobs.GetBlobContainerClient(container);
        var source = containerClient.GetBlobClient(sourceName);
        var props = (await source.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
        if (props.BlobType != BlobType.Block)
            throw new InvalidOperationException($"'{BlobPaths.LeafName(sourceName)}' is a {props.BlobType.ToString().ToLowerInvariant()} blob. Only block blobs can be renamed here; use AzCopy or Storage Explorer for page and append blobs.");
        if (string.Equals(props.AccessTier, "Archive", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{BlobPaths.LeafName(sourceName)}' is in the Archive tier and must be rehydrated before it can be renamed.");

        // Put Blob From URL needs the source authorised separately, with the same Entra ID token.
        var token = await _credential.GetTokenAsync(new TokenRequestContext([AzureAuth.StorageScope]), ct).ConfigureAwait(false);
        var sourceAuth = new HttpAuthorization("Bearer", token.Token);
        var target = containerClient.GetBlockBlobClient(targetName);
        var headers = new BlobHttpHeaders
        {
            ContentType = props.ContentType,
            ContentEncoding = props.ContentEncoding,
            ContentLanguage = props.ContentLanguage,
            ContentDisposition = props.ContentDisposition,
            CacheControl = props.CacheControl,
            ContentHash = props.ContentHash,
        };
        var noReplace = new BlobRequestConditions { IfNoneMatch = ETag.All };
        AccessTier? tier = props.AccessTierInferred || string.IsNullOrEmpty(props.AccessTier) ? null : new AccessTier(props.AccessTier);

        try
        {
            if (props.ContentLength <= SingleCopyLimit)
            {
                await target.SyncUploadFromUriAsync(source.Uri, new BlobSyncUploadFromUriOptions
                {
                    SourceAuthentication = sourceAuth,
                    CopySourceBlobProperties = false,
                    HttpHeaders = headers,
                    AccessTier = tier,
                    DestinationConditions = noReplace,
                    SourceConditions = new BlobRequestConditions { IfMatch = props.ETag },
                }, ct).ConfigureAwait(false);
                progress?.Report(props.ContentLength);
            }
            else
            {
                var blockIds = new List<string>();
                long offset = 0;
                var index = 0;
                while (offset < props.ContentLength)
                {
                    var length = Math.Min(CopyBlockSize, props.ContentLength - offset);
                    var id = Convert.ToBase64String(BitConverter.GetBytes(index++).Concat(new byte[4]).ToArray());
                    await target.StageBlockFromUriAsync(source.Uri, id, new StageBlockFromUriOptions
                    {
                        SourceRange = new HttpRange(offset, length),
                        SourceAuthentication = sourceAuth,
                        SourceConditions = new RequestConditions { IfMatch = props.ETag },
                    }, ct).ConfigureAwait(false);
                    blockIds.Add(id);
                    offset += length;
                    progress?.Report(offset);
                }
                await target.CommitBlockListAsync(blockIds, new CommitBlockListOptions
                {
                    HttpHeaders = headers,
                    AccessTier = tier,
                    Conditions = noReplace,
                }, ct).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 409 && ex.ErrorCode == "BlobAlreadyExists" || ex.Status == 412 && ex.ErrorCode != "SourceConditionNotMet")
        {
            throw new StorageTargetExistsException(BlobPaths.LeafName(targetName));
        }

        if (props.Metadata.Count > 0)
            await target.SetMetadataAsync(props.Metadata, cancellationToken: ct).ConfigureAwait(false);
    }
}
