namespace RemoteConnectionManager.AppService;

/// <summary>A Web App or Function App found while listing a subscription.</summary>
public sealed record WebAppInfo(
    string Name,
    string ResourceId,
    string SubscriptionId,
    string SubscriptionName,
    string ResourceGroup,
    string Location,
    string Kind,
    string? State,
    string? DefaultHostName,
    string? TenantId)
{
    public bool IsFunctionApp => WebAppKinds.IsFunctionApp(Kind);
}

/// <summary>What the portal shows on a Web App's Overview page, for the site or one slot.</summary>
public sealed record WebAppOverview
{
    public required string Name { get; init; }
    public string? SlotName { get; init; }
    public required string ResourceId { get; init; }
    public required string ResourceGroup { get; init; }
    public required string SubscriptionId { get; init; }
    public required string Location { get; init; }
    public string Kind { get; init; } = string.Empty;
    /// <summary>"Running" or "Stopped", as Azure reports it.</summary>
    public string? State { get; init; }
    public bool Enabled { get; init; } = true;
    public string? AvailabilityState { get; init; }
    public string? DefaultHostName { get; init; }
    public IReadOnlyList<string> CustomDomains { get; init; } = Array.Empty<string>();
    public bool IsLinux { get; init; }
    public bool? HttpsOnly { get; init; }
    public string? AppServicePlanName { get; init; }
    public string? AppServicePlanId { get; init; }
    public string? PricingTier { get; init; }
    public int? InstanceCount { get; init; }
    public string? RuntimeStack { get; init; }
    public bool? AlwaysOn { get; init; }
    public string? MinTlsVersion { get; init; }
    public string? FtpsState { get; init; }
    public string? HealthCheckPath { get; init; }
    public bool? Http20 { get; init; }
    public bool? WebSockets { get; init; }
    public bool? Use32BitWorker { get; init; }
    public string? PublicNetworkAccess { get; init; }
    public string? VirtualNetworkSubnet { get; init; }
    public string? ManagedIdentity { get; init; }
    public bool? ClientCertificates { get; init; }
    public IReadOnlyList<string> OutboundIpAddresses { get; init; } = Array.Empty<string>();
    public DateTimeOffset? LastModified { get; init; }
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    /// <summary>Why some of the extra details could not be read (for example no access to the App Service plan).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);
    public bool IsStopped => string.Equals(State, "Stopped", StringComparison.OrdinalIgnoreCase);
    public bool IsFunctionApp => WebAppKinds.IsFunctionApp(Kind);
}

/// <summary>An app setting (an environment variable in the portal). The value is a secret until shown.</summary>
public sealed record AppSettingValue(string Name, string Value, bool SlotSetting);

/// <summary>A connection string. Type is one of <see cref="WebAppSettings.ConnectionStringTypes"/>.</summary>
public sealed record ConnectionStringValue(string Name, string Value, string Type, bool SlotSetting);

/// <summary>
/// The app settings and connection strings of the site or a slot, with each one's "deployment slot
/// setting" flag. Kept as loaded, so a save can tell whether someone else changed them meanwhile.
/// </summary>
public sealed record WebAppSettings(IReadOnlyList<AppSettingValue> AppSettings, IReadOnlyList<ConnectionStringValue> ConnectionStrings)
{
    public static WebAppSettings Empty { get; } = new(Array.Empty<AppSettingValue>(), Array.Empty<ConnectionStringValue>());

    /// <summary>The connection string types Azure accepts, in the portal's order.</summary>
    public static IReadOnlyList<string> ConnectionStringTypes { get; } =
        ["SQLAzure", "SQLServer", "MySql", "PostgreSQL", "Custom", "NotificationHub", "ServiceBus", "EventHub", "ApiHub", "DocDb", "RedisCache"];
}

public static class WebAppKinds
{
    public static bool IsFunctionApp(string? kind) => kind?.Contains("functionapp", StringComparison.OrdinalIgnoreCase) ?? false;
    public static bool IsLinux(string? kind) => kind?.Contains("linux", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>"Web App", "Function App", with "(Linux)" or "(container)" when the kind says so.</summary>
    public static string Describe(string? kind)
    {
        var name = IsFunctionApp(kind) ? "Function App" : "Web App";
        if (kind is null) return name;
        var extras = new List<string>();
        if (IsLinux(kind)) extras.Add("Linux");
        if (kind.Contains("container", StringComparison.OrdinalIgnoreCase)) extras.Add("container");
        return extras.Count == 0 ? name : $"{name} ({string.Join(", ", extras)})";
    }
}

/// <summary>Thrown by a save when the settings in Azure no longer match the ones the edits were based on.</summary>
public sealed class SettingsChangedElsewhereException : Exception
{
    public SettingsChangedElsewhereException()
        : base("The settings were changed in Azure after you loaded them.")
    {
    }
}
