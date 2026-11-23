using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Models;

namespace RemoteConnectionManager.AppService;

/// <summary>
/// Reads and changes one Web App (or one of its slots) through Azure Resource Manager. Azure IAM
/// decides what is allowed: Reader can see the overview, reading or saving settings needs Website
/// Contributor or Contributor. Setting values are never logged here.
/// </summary>
public sealed class WebAppClient
{
    private readonly ArmClient _arm;

    public WebAppClient(WebAppId id, TokenCredential credential)
    {
        Id = id;
        _arm = new ArmClient(credential);
    }

    public WebAppId Id { get; }

    private WebSiteResource Site(WebAppId id) => _arm.GetWebSiteResource(new ResourceIdentifier(id.Site.ToString()));
    private WebSiteSlotResource Slot(WebAppId id) => _arm.GetWebSiteSlotResource(new ResourceIdentifier(id.ToString()));

    // ---------- overview ----------

    public async Task<WebAppOverview> GetOverviewAsync(WebAppId id, CancellationToken ct)
    {
        WebSiteData data;
        SiteConfigData? config = null;
        var warnings = new List<string>();

        if (id.IsSlot)
        {
            var slot = (await Slot(id).GetAsync(ct).ConfigureAwait(false)).Value;
            data = slot.Data;
            try { config = (await slot.GetWebSiteSlotConfig().GetAsync(ct).ConfigureAwait(false)).Value.Data; }
            catch (RequestFailedException ex) { warnings.Add($"Configuration not read ({ex.Status})."); }
        }
        else
        {
            var site = (await Site(id).GetAsync(ct).ConfigureAwait(false)).Value;
            data = site.Data;
            try { config = (await site.GetWebSiteConfig().GetAsync(ct).ConfigureAwait(false)).Value.Data; }
            catch (RequestFailedException ex) { warnings.Add($"Configuration not read ({ex.Status})."); }
        }

        string? planName = null, tier = null;
        int? instances = null;
        if (data.AppServicePlanId is { } planId)
        {
            planName = planId.Name;
            try
            {
                var plan = (await _arm.GetAppServicePlanResource(planId).GetAsync(ct).ConfigureAwait(false)).Value.Data;
                planName = plan.Name;
                if (plan.Sku is { } sku) tier = string.IsNullOrEmpty(sku.Tier) ? sku.Name : $"{sku.Tier} ({sku.Name})";
                instances = plan.Sku?.Capacity ?? plan.NumberOfWorkers;
            }
            catch (RequestFailedException ex)
            {
                warnings.Add(ex.Status == 403 ? "No access to read the App Service plan." : $"App Service plan not read ({ex.Status}).");
            }
        }
        tier ??= data.Sku;

        IEnumerable<string> hostNames = data.HostNames ?? (IEnumerable<string>)Array.Empty<string>();
        var custom = hostNames.Where(h => !string.Equals(h, data.DefaultHostName, StringComparison.OrdinalIgnoreCase)).ToList();

        return new WebAppOverview
        {
            Name = id.SiteName,
            SlotName = id.SlotName,
            ResourceId = id.ToString(),
            ResourceGroup = id.ResourceGroup,
            SubscriptionId = id.SubscriptionId,
            Location = data.Location.DisplayName ?? data.Location.ToString(),
            Kind = data.Kind ?? string.Empty,
            State = data.State,
            Enabled = data.IsEnabled ?? true,
            AvailabilityState = data.AvailabilityState?.ToString(),
            DefaultHostName = data.DefaultHostName,
            CustomDomains = custom,
            IsLinux = data.IsReserved == true || WebAppKinds.IsLinux(data.Kind),
            HttpsOnly = data.IsHttpsOnly,
            AppServicePlanName = planName,
            AppServicePlanId = data.AppServicePlanId?.ToString(),
            PricingTier = tier,
            InstanceCount = instances,
            RuntimeStack = DescribeStack(config, data.Kind),
            AlwaysOn = config?.IsAlwaysOn,
            MinTlsVersion = config?.MinTlsVersion?.ToString(),
            FtpsState = config?.FtpsState?.ToString(),
            HealthCheckPath = config?.HealthCheckPath,
            Http20 = config?.IsHttp20Enabled,
            WebSockets = config?.IsWebSocketsEnabled,
            Use32BitWorker = config?.Use32BitWorkerProcess,
            PublicNetworkAccess = data.PublicNetworkAccess,
            VirtualNetworkSubnet = data.VirtualNetworkSubnetId?.ToString(),
            ManagedIdentity = DescribeIdentity(data.Identity),
            ClientCertificates = data.IsClientCertEnabled,
            OutboundIpAddresses = (data.OutboundIPAddresses ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            LastModified = data.LastModifiedTimeUtc is { } m ? new DateTimeOffset(DateTime.SpecifyKind(m.UtcDateTime, DateTimeKind.Utc)) : null,
            Tags = new Dictionary<string, string>(data.Tags ?? new Dictionary<string, string>()),
            Warnings = warnings,
        };
    }

    /// <summary>Plain words for the runtime, from the site configuration (for example "DOTNETCORE 8.0" or ".NET v8.0").</summary>
    internal static string? DescribeStack(SiteConfigData? config, string? kind)
    {
        if (config is null) return null;
        static string Fx(string value) => value.Replace('|', ' ').Trim();
        if (!string.IsNullOrWhiteSpace(config.LinuxFxVersion)) return Fx(config.LinuxFxVersion);
        if (!string.IsNullOrWhiteSpace(config.WindowsFxVersion)) return Fx(config.WindowsFxVersion);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.JavaVersion))
            parts.Add($"Java {config.JavaVersion}" + (string.IsNullOrWhiteSpace(config.JavaContainer) ? string.Empty : $" ({config.JavaContainer} {config.JavaContainerVersion})".TrimEnd()));
        if (!string.IsNullOrWhiteSpace(config.PhpVersion)) parts.Add($"PHP {config.PhpVersion}");
        if (!string.IsNullOrWhiteSpace(config.PythonVersion)) parts.Add($"Python {config.PythonVersion}");
        if (!string.IsNullOrWhiteSpace(config.NodeVersion)) parts.Add($"Node {config.NodeVersion}");
        if (!string.IsNullOrWhiteSpace(config.PowerShellVersion)) parts.Add($"PowerShell {config.PowerShellVersion}");
        if (!string.IsNullOrWhiteSpace(config.NetFrameworkVersion)) parts.Add($".NET {config.NetFrameworkVersion}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string DescribeIdentity(ManagedServiceIdentity? identity)
    {
        if (identity is null) return "None";
        var type = identity.ManagedServiceIdentityType.ToString();
        if (type.Equals("None", StringComparison.OrdinalIgnoreCase)) return "None";
        var system = type.Contains("SystemAssigned", StringComparison.OrdinalIgnoreCase);
        var users = identity.UserAssignedIdentities?.Count ?? 0;
        var parts = new List<string>();
        if (system) parts.Add("System assigned");
        if (users > 0) parts.Add($"{users} user assigned");
        return parts.Count == 0 ? type : string.Join(", ", parts);
    }

    // ---------- slots ----------

    /// <summary>The site's deployment slot names (not including production).</summary>
    public async Task<IReadOnlyList<string>> ListSlotNamesAsync(CancellationToken ct)
    {
        var list = new List<string>();
        await foreach (var slot in Site(Id).GetWebSiteSlots().GetAllAsync(ct).ConfigureAwait(false))
        {
            // The slot's name comes back as "site/slot".
            var name = slot.Id.Name;
            var slash = name.LastIndexOf('/');
            list.Add(slash >= 0 ? name[(slash + 1)..] : name);
        }
        return list.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- start, stop, restart ----------

    public Task StartAsync(WebAppId id, CancellationToken ct) =>
        id.IsSlot ? Slot(id).StartSlotAsync(ct) : Site(id).StartAsync(ct);

    public Task StopAsync(WebAppId id, CancellationToken ct) =>
        id.IsSlot ? Slot(id).StopSlotAsync(ct) : Site(id).StopAsync(ct);

    public Task RestartAsync(WebAppId id, CancellationToken ct) =>
        id.IsSlot ? Slot(id).RestartSlotAsync(softRestart: null, synchronous: null, ct) : Site(id).RestartAsync(softRestart: null, synchronous: null, ct);

    // ---------- settings ----------

    /// <summary>App settings and connection strings, with the slot setting ticks from the production site.</summary>
    public async Task<WebAppSettings> GetSettingsAsync(WebAppId id, CancellationToken ct)
    {
        AppServiceConfigurationDictionary appSettings;
        ConnectionStringDictionary connectionStrings;
        if (id.IsSlot)
        {
            var slot = Slot(id);
            appSettings = (await slot.GetApplicationSettingsSlotAsync(ct).ConfigureAwait(false)).Value;
            connectionStrings = (await slot.GetConnectionStringsSlotAsync(ct).ConfigureAwait(false)).Value;
        }
        else
        {
            var site = Site(id);
            appSettings = (await site.GetApplicationSettingsAsync(ct).ConfigureAwait(false)).Value;
            connectionStrings = (await site.GetConnectionStringsAsync(ct).ConfigureAwait(false)).Value;
        }
        var names = await GetSlotNamesAsync(id, ct).ConfigureAwait(false);
        var slotSettings = new HashSet<string>(names?.AppSettingNames ?? (IEnumerable<string>)Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var slotConnections = new HashSet<string>(names?.ConnectionStringNames ?? (IEnumerable<string>)Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var settings = appSettings.Properties
            .Select(p => new AppSettingValue(p.Key, p.Value ?? string.Empty, slotSettings.Contains(p.Key)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var connections = connectionStrings.Properties
            .Select(p => new ConnectionStringValue(p.Key, p.Value?.Value ?? string.Empty, TypeName(p.Value?.ConnectionStringType), slotConnections.Contains(p.Key)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new WebAppSettings(settings, connections);
    }

    private async Task<SlotConfigNamesResourceData?> GetSlotNamesAsync(WebAppId id, CancellationToken ct)
    {
        try
        {
            return (await Site(id).GetSlotConfigNamesResource().GetAsync(ct).ConfigureAwait(false)).Value.Data;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves edited settings. Reads them again first and refuses with
    /// <see cref="SettingsChangedElsewhereException"/> if they no longer match <paramref name="original"/>,
    /// so two people editing at once cannot silently overwrite each other. Azure has no lock for this,
    /// so a change made in the second between the check and the save can still be lost.
    /// Replacing app settings or connection strings restarts the app.
    /// </summary>
    public async Task SaveSettingsAsync(WebAppId id, WebAppSettings original, WebAppSettings edited, CancellationToken ct)
    {
        var plan = SettingsPlan.Create(original, edited);
        if (!plan.HasChanges) return;
        if (SettingsPlan.Validate(edited) is { } problem) throw new InvalidOperationException(problem);

        var current = await GetSettingsAsync(id, ct).ConfigureAwait(false);
        if (!SettingsPlan.Same(original, current)) throw new SettingsChangedElsewhereException();

        // Slot ticks first: if this part is refused, nothing has restarted the app yet.
        if (plan.SlotFlagsChanged)
        {
            var namesResource = Site(id).GetSlotConfigNamesResource();
            var names = await GetSlotNamesAsync(id, ct).ConfigureAwait(false);
            var data = new SlotConfigNamesResourceData();
            foreach (var n in SettingsPlan.MergeSlotNames(names?.AppSettingNames, original.AppSettings, edited.AppSettings, s => s.Name, s => s.SlotSetting))
                data.AppSettingNames.Add(n);
            foreach (var n in SettingsPlan.MergeSlotNames(names?.ConnectionStringNames, original.ConnectionStrings, edited.ConnectionStrings, c => c.Name, c => c.SlotSetting))
                data.ConnectionStringNames.Add(n);
            foreach (var n in names?.AzureStorageConfigNames ?? (IEnumerable<string>)Array.Empty<string>())
                data.AzureStorageConfigNames.Add(n);
            await namesResource.CreateOrUpdateAsync(WaitUntil.Completed, data, ct).ConfigureAwait(false);
        }

        if (plan.AppSettingsChanged)
        {
            var body = new AppServiceConfigurationDictionary();
            foreach (var s in edited.AppSettings) body.Properties[s.Name] = s.Value;
            if (id.IsSlot) await Slot(id).UpdateApplicationSettingsSlotAsync(body, ct).ConfigureAwait(false);
            else await Site(id).UpdateApplicationSettingsAsync(body, ct).ConfigureAwait(false);
        }

        if (plan.ConnectionStringsChanged)
        {
            var body = new ConnectionStringDictionary();
            foreach (var c in edited.ConnectionStrings) body.Properties[c.Name] = new ConnStringValueTypePair(c.Value, ParseType(c.Type));
            if (id.IsSlot) await Slot(id).UpdateConnectionStringsSlotAsync(body, ct).ConfigureAwait(false);
            else await Site(id).UpdateConnectionStringsAsync(body, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The portal's name for a connection string type (Azure's own spelling, for example "SQLAzure").</summary>
    internal static string TypeName(ConnectionStringType? type)
    {
        if (type is not { } t) return "Custom";
        var text = t.ToString();
        return WebAppSettings.ConnectionStringTypes.FirstOrDefault(n => n.Equals(text, StringComparison.OrdinalIgnoreCase)) ?? text;
    }

    internal static ConnectionStringType ParseType(string type) =>
        Enum.TryParse<ConnectionStringType>(type, ignoreCase: true, out var parsed) ? parsed : ConnectionStringType.Custom;
}
