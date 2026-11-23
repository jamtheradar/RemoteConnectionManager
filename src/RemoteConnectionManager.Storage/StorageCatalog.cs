using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using RemoteConnectionManager.KeyVault;

namespace RemoteConnectionManager.Storage;

/// <summary>Finds the storage accounts in a subscription through Azure Resource Manager.</summary>
public sealed class StorageCatalog
{
    private readonly AzureAuth _auth;

    public StorageCatalog(AzureAuth auth)
    {
        _auth = auth;
    }

    public async Task<IReadOnlyList<StorageAccountInfo>> GetAccountsAsync(SubscriptionInfo subscription, CancellationToken ct)
    {
        // A subscription in another tenant needs a token from that tenant.
        var tenant = string.Equals(subscription.TenantId, _auth.HomeTenantId, StringComparison.OrdinalIgnoreCase) ? null : subscription.TenantId;
        var arm = new ArmClient(_auth.Credential(tenant));
        var sub = arm.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscription.Id));

        var list = new List<StorageAccountInfo>();
        await foreach (var account in sub.GetStorageAccountsAsync(ct).ConfigureAwait(true))
        {
            var data = account.Data;
            list.Add(new StorageAccountInfo(
                data.Name,
                account.Id.ToString(),
                subscription.Id,
                subscription.Name,
                account.Id.ResourceGroupName ?? string.Empty,
                data.Location.DisplayName ?? data.Location.ToString(),
                data.Kind?.ToString() ?? "StorageV2",
                data.Sku?.Name.ToString(),
                data.IsHnsEnabled == true,
                subscription.TenantId));
        }
        return list.OrderBy(a => a.ResourceGroup, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"Storage account (StorageV2, Standard_LRS, Data Lake)" style detail for the browser tree.</summary>
    public static string Describe(StorageAccountInfo account)
    {
        var parts = new List<string> { account.Kind };
        if (!string.IsNullOrEmpty(account.Sku)) parts.Add(account.Sku);
        if (account.HierarchicalNamespace) parts.Add("Data Lake");
        return $"Storage ({string.Join(", ", parts)}) · {account.Location}";
    }
}
