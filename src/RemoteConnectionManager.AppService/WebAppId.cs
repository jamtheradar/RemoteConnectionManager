namespace RemoteConnectionManager.AppService;

/// <summary>
/// The Azure resource ID of a Web App or one of its deployment slots:
/// /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Web/sites/{name}[/slots/{slot}].
/// </summary>
public sealed record WebAppId(string SubscriptionId, string ResourceGroup, string SiteName, string? SlotName = null)
{
    public bool IsSlot => SlotName is not null;

    /// <summary>The production site this slot belongs to (itself when it is not a slot).</summary>
    public WebAppId Site => SlotName is null ? this : this with { SlotName = null };

    public WebAppId ForSlot(string? slot) => this with { SlotName = string.IsNullOrWhiteSpace(slot) ? null : slot };

    /// <summary>"name" or "name/slot", the way the portal titles a slot.</summary>
    public string DisplayName => SlotName is null ? SiteName : $"{SiteName}/{SlotName}";

    public override string ToString() =>
        $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/Microsoft.Web/sites/{SiteName}"
        + (SlotName is null ? string.Empty : $"/slots/{SlotName}");

    /// <summary>
    /// Reads a resource ID, or an Azure portal address that contains one (anything after the ID, such as
    /// /overview or /configuration, is ignored). Null when it is not a Microsoft.Web/sites resource.
    /// </summary>
    public static WebAppId? Parse(string? text)
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
        // subscriptions {sub} resourceGroups {rg} providers Microsoft.Web sites {name} [slots {slot}] [anything]
        if (parts.Length < 8) return null;
        if (!Is(parts[0], "subscriptions") || !Is(parts[2], "resourceGroups") || !Is(parts[4], "providers")
            || !Is(parts[5], "Microsoft.Web") || !Is(parts[6], "sites"))
            return null;
        if (!Guid.TryParse(parts[1], out _)) return null;
        var rg = parts[3];
        var name = parts[7];
        if (rg.Length == 0 || name.Length == 0) return null;
        string? slot = null;
        if (parts.Length >= 10 && Is(parts[8], "slots") && parts[9].Length > 0) slot = parts[9];
        return new WebAppId(parts[1].ToLowerInvariant(), rg, name, slot);
    }

    /// <summary>The resource in the Azure portal. The tenant (an ID or domain) keeps the portal in the right directory.</summary>
    public string PortalUrl(string? tenant)
    {
        var at = string.IsNullOrWhiteSpace(tenant) ? string.Empty : "@" + tenant.Trim();
        return $"https://portal.azure.com/#{at}/resource{this}/overview";
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>An app setting whose value points at Key Vault: @Microsoft.KeyVault(...).</summary>
public sealed record KeyVaultReference(Uri VaultUri, string SecretName, string? Version)
{
    private const string Prefix = "@Microsoft.KeyVault(";

    public static bool LooksLikeReference(string? value) =>
        value is not null && value.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads either form: SecretUri=https://kv.vault.azure.net/secrets/name[/version], or
    /// VaultName=kv;SecretName=name[;SecretVersion=v]. Null when the value is not a reference
    /// or cannot be read.
    /// </summary>
    public static KeyVaultReference? Parse(string? value)
    {
        if (!LooksLikeReference(value)) return null;
        var text = value!.Trim();
        var end = text.LastIndexOf(')');
        if (end < Prefix.Length) return null;
        var body = text[Prefix.Length..end];

        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            pairs[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }

        if (pairs.TryGetValue("SecretUri", out var secretUri))
        {
            if (!Uri.TryCreate(secretUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || !segments[0].Equals("secrets", StringComparison.OrdinalIgnoreCase)) return null;
            var vault = new Uri($"https://{uri.Host}/");
            return new KeyVaultReference(vault, segments[1], segments.Length > 2 ? segments[2] : null);
        }

        if (pairs.TryGetValue("VaultName", out var vaultName) && pairs.TryGetValue("SecretName", out var secretName)
            && vaultName.Length > 0 && secretName.Length > 0)
        {
            var vault = vaultName.Contains('.', StringComparison.Ordinal) ? $"https://{vaultName}/" : $"https://{vaultName}.vault.azure.net/";
            if (!Uri.TryCreate(vault, UriKind.Absolute, out var vaultUri)) return null;
            pairs.TryGetValue("SecretVersion", out var version);
            return new KeyVaultReference(vaultUri, secretName, string.IsNullOrEmpty(version) ? null : version);
        }

        return null;
    }
}
