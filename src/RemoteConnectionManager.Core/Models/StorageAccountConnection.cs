using System.Text.Json.Serialization;

namespace RemoteConnectionManager.Core.Models;

/// <summary>
/// An Azure Storage account (Microsoft.Storage/storageAccounts) opened in a blob browser tab. Signs in
/// with Entra ID only (Storage Blob Data roles); no account keys or SAS tokens. Stores only where it is.
/// </summary>
public sealed class StorageAccountConnection : ConnectionEntry
{
    /// <summary>/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{name}.</summary>
    public string ResourceId { get; set; } = string.Empty;
    /// <summary>The tenant the subscription is in, when it is not the signed in account's home tenant.</summary>
    public string? TenantId { get; set; }
    /// <summary>Optional container to open first.</summary>
    public string? Container { get; set; }

    [JsonIgnore]
    public override ConnectionKind Kind => ConnectionKind.StorageAccount;
    [JsonIgnore]
    public override string Prefix => "(STG)";

    public override ConnectionEntry Clone()
    {
        var copy = new StorageAccountConnection { ResourceId = ResourceId, TenantId = TenantId, Container = Container };
        CopyBaseTo(copy);
        return copy;
    }
}
