namespace RemoteConnectionManager.Storage;

/// <summary>
/// The Azure resource ID of a storage account:
/// /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{name}.
/// </summary>
public sealed record StorageAccountId(string SubscriptionId, string ResourceGroup, string AccountName)
{
    public override string ToString() =>
        $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/Microsoft.Storage/storageAccounts/{AccountName}";

    /// <summary>The public cloud blob endpoint, used when the account's own endpoints cannot be read from Azure.</summary>
    public Uri DefaultBlobEndpoint => new($"https://{AccountName}.blob.core.windows.net/");

    /// <summary>
    /// Reads a resource ID, or an Azure portal address that contains one (anything after the ID, such as
    /// /overview or /containersList, is ignored). Null when it is not a Microsoft.Storage/storageAccounts resource.
    /// </summary>
    public static StorageAccountId? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        var start = value.IndexOf("/subscriptions/", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            if (!value.StartsWith("subscriptions/", StringComparison.OrdinalIgnoreCase)) return null;
            value = "/" + value;
            start = 0;
        }
        value = value[start..];
        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0) value = value[..cut];

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        // subscriptions {sub} resourceGroups {rg} providers Microsoft.Storage storageAccounts {name} [anything]
        if (parts.Length < 8) return null;
        if (!Is(parts[0], "subscriptions") || !Is(parts[2], "resourceGroups") || !Is(parts[4], "providers")
            || !Is(parts[5], "Microsoft.Storage") || !Is(parts[6], "storageAccounts"))
            return null;
        if (!Guid.TryParse(parts[1], out _)) return null;
        var rg = parts[3];
        var name = parts[7];
        if (rg.Length == 0 || !IsValidAccountName(name)) return null;
        return new StorageAccountId(parts[1].ToLowerInvariant(), rg, name.ToLowerInvariant());
    }

    /// <summary>3 to 24 lower case letters and digits.</summary>
    public static bool IsValidAccountName(string? name) =>
        name is { Length: >= 3 and <= 24 } && name.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetter(c));

    /// <summary>The account in the Azure portal. The tenant (an ID or domain) keeps the portal in the right directory.</summary>
    public string PortalUrl(string? tenant)
    {
        var at = string.IsNullOrWhiteSpace(tenant) ? string.Empty : "@" + tenant.Trim();
        return $"https://portal.azure.com/#{at}/resource{this}/overview";
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
