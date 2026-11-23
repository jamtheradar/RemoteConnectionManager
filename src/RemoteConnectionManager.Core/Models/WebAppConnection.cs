using System.Text.Json.Serialization;

namespace RemoteConnectionManager.Core.Models;

/// <summary>
/// An Azure Web App or Function App (Microsoft.Web/sites), or one of its deployment slots, opened in
/// a tab like the portal's Overview and Environment variables pages. Stores only where it is.
/// </summary>
public sealed class WebAppConnection : ConnectionEntry
{
    /// <summary>/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Web/sites/{name}[/slots/{slot}].</summary>
    public string ResourceId { get; set; } = string.Empty;
    /// <summary>The tenant the subscription is in, when it is not the signed in account's home tenant.</summary>
    public string? TenantId { get; set; }

    [JsonIgnore]
    public override ConnectionKind Kind => ConnectionKind.WebApp;
    [JsonIgnore]
    public override string Prefix => "(APP)";

    public override ConnectionEntry Clone()
    {
        var copy = new WebAppConnection { ResourceId = ResourceId, TenantId = TenantId };
        CopyBaseTo(copy);
        return copy;
    }
}
