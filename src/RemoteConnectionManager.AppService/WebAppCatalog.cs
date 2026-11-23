using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Resources;
using RemoteConnectionManager.KeyVault;

namespace RemoteConnectionManager.AppService;

/// <summary>Finds the Web Apps and Function Apps in a subscription through Azure Resource Manager.</summary>
public sealed class WebAppCatalog
{
    private readonly AzureAuth _auth;

    public WebAppCatalog(AzureAuth auth)
    {
        _auth = auth;
    }

    public async Task<IReadOnlyList<WebAppInfo>> GetWebAppsAsync(SubscriptionInfo subscription, CancellationToken ct)
    {
        // A subscription in another tenant needs a token from that tenant.
        var tenant = string.Equals(subscription.TenantId, _auth.HomeTenantId, StringComparison.OrdinalIgnoreCase) ? null : subscription.TenantId;
        var arm = new ArmClient(_auth.Credential(tenant));
        var sub = arm.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscription.Id));

        var list = new List<WebAppInfo>();
        await foreach (var site in sub.GetWebSitesAsync(ct).ConfigureAwait(true))
        {
            var data = site.Data;
            list.Add(new WebAppInfo(
                data.Name,
                site.Id.ToString(),
                subscription.Id,
                subscription.Name,
                site.Id.ResourceGroupName ?? data.ResourceGroup ?? string.Empty,
                data.Location.DisplayName ?? data.Location.ToString(),
                data.Kind ?? "app",
                data.State,
                data.DefaultHostName,
                subscription.TenantId));
        }
        return list.OrderBy(a => a.ResourceGroup, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
