using System.Text.Json;
using RemoteConnectionManager.AppService;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.Storage;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class WebAppTests
{
    private const string Sub = "0b1f6471-1bf0-4dda-aec3-111122223333";
    private const string SiteId = "/subscriptions/" + Sub + "/resourceGroups/rg-web/providers/Microsoft.Web/sites/app-prod";

    // ---------- resource IDs ----------

    [Fact]
    public void Parse_ReadsSiteResourceId()
    {
        var id = WebAppId.Parse(SiteId);
        Assert.NotNull(id);
        Assert.Equal(Sub, id!.SubscriptionId);
        Assert.Equal("rg-web", id.ResourceGroup);
        Assert.Equal("app-prod", id.SiteName);
        Assert.Null(id.SlotName);
        Assert.Equal(SiteId, id.ToString());
    }

    [Fact]
    public void Parse_ReadsSlotAndKeepsTheSite()
    {
        var id = WebAppId.Parse(SiteId + "/slots/staging");
        Assert.Equal("staging", id!.SlotName);
        Assert.Equal("app-prod/staging", id.DisplayName);
        Assert.Equal(SiteId, id.Site.ToString());
        Assert.Equal(SiteId + "/slots/staging", id.ToString());
    }

    [Theory]
    [InlineData("https://portal.azure.com/#@contoso.onmicrosoft.com/resource" + SiteId + "/overview")]
    [InlineData("https://portal.azure.com/#view/resource" + SiteId + "/configuration?x=1")]
    [InlineData("subscriptions/" + Sub + "/resourceGroups/rg-web/providers/Microsoft.Web/sites/app-prod")]
    [InlineData("  /SUBSCRIPTIONS/" + Sub + "/resourcegroups/rg-web/PROVIDERS/microsoft.web/Sites/app-prod  ")]
    public void Parse_AcceptsPortalAddressesAndOddCasing(string text)
    {
        var id = WebAppId.Parse(text);
        Assert.NotNull(id);
        Assert.Equal("app-prod", id!.SiteName);
        Assert.Equal("rg-web", id.ResourceGroup);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("app-prod")]
    [InlineData("https://app-prod.azurewebsites.net")]
    [InlineData("/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv")]
    [InlineData("/subscriptions/not-a-guid/resourceGroups/rg/providers/Microsoft.Web/sites/app")]
    [InlineData("/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Web/serverfarms/plan")]
    public void Parse_RejectsAnythingElse(string? text) => Assert.Null(WebAppId.Parse(text));

    [Fact]
    public void PortalUrl_IncludesTheTenantWhenKnown()
    {
        var id = WebAppId.Parse(SiteId)!;
        Assert.Equal($"https://portal.azure.com/#@tenant-a/resource{SiteId}/overview", id.PortalUrl("tenant-a"));
        Assert.Equal($"https://portal.azure.com/#/resource{SiteId}/overview", id.PortalUrl(null));
    }

    // ---------- Key Vault references ----------

    [Fact]
    public void KeyVaultReference_ReadsSecretUriForm()
    {
        var r = KeyVaultReference.Parse("@Microsoft.KeyVault(SecretUri=https://kv-prod.vault.azure.net/secrets/DbPassword/abc123)");
        Assert.NotNull(r);
        Assert.Equal(new Uri("https://kv-prod.vault.azure.net/"), r!.VaultUri);
        Assert.Equal("DbPassword", r.SecretName);
        Assert.Equal("abc123", r.Version);
    }

    [Fact]
    public void KeyVaultReference_ReadsVaultNameForm()
    {
        var r = KeyVaultReference.Parse("@Microsoft.KeyVault(VaultName=kv-prod; SecretName=DbPassword)");
        Assert.Equal(new Uri("https://kv-prod.vault.azure.net/"), r!.VaultUri);
        Assert.Equal("DbPassword", r.SecretName);
        Assert.Null(r.Version);
    }

    [Theory]
    [InlineData("plain value")]
    [InlineData("@Microsoft.KeyVault()")]
    [InlineData("@Microsoft.KeyVault(SecretUri=http://kv.vault.azure.net/secrets/x)")]
    [InlineData("@Microsoft.KeyVault(VaultName=kv)")]
    public void KeyVaultReference_RejectsBadValues(string value) => Assert.Null(KeyVaultReference.Parse(value));

    [Fact]
    public void KeyVaultReference_LooksLikeReferenceIgnoresCase() =>
        Assert.True(KeyVaultReference.LooksLikeReference("  @microsoft.keyvault(whatever"));

    // ---------- save plan ----------

    private static WebAppSettings Settings(params AppSettingValue[] settings) => new(settings, Array.Empty<ConnectionStringValue>());

    [Fact]
    public void Plan_NoChanges_WhenOnlyOrderDiffers()
    {
        var a = Settings(new AppSettingValue("A", "1", false), new("B", "2", true));
        var b = Settings(new AppSettingValue("B", "2", true), new("A", "1", false));
        var plan = SettingsPlan.Create(a, b);
        Assert.False(plan.HasChanges);
        Assert.True(SettingsPlan.Same(a, b));
    }

    [Fact]
    public void Plan_ListsAddedChangedRemovedByNameOnly()
    {
        var before = Settings(new AppSettingValue("KEEP", "1", false), new("EDIT", "old-secret", false), new("GONE", "x", false));
        var after = Settings(new AppSettingValue("KEEP", "1", false), new("EDIT", "new-secret", false), new("NEW", "y", false));
        var plan = SettingsPlan.Create(before, after);

        Assert.Equal(["NEW"], plan.AddedSettings);
        Assert.Equal(["EDIT"], plan.ChangedSettings);
        Assert.Equal(["GONE"], plan.RemovedSettings);
        Assert.True(plan.AppSettingsChanged);
        Assert.False(plan.ConnectionStringsChanged);
        Assert.True(plan.RestartsApp);
        var text = string.Join("\n", plan.Describe());
        Assert.Contains("EDIT", text);
        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public void Plan_SlotTickAlone_DoesNotResendTheList()
    {
        var plan = SettingsPlan.Create(Settings(new AppSettingValue("A", "1", false)), Settings(new AppSettingValue("A", "1", true)));
        Assert.True(plan.HasChanges);
        Assert.True(plan.SlotFlagsChanged);
        Assert.False(plan.AppSettingsChanged);
        Assert.False(plan.RestartsApp);
    }

    [Fact]
    public void Plan_ConnectionStringTypeChangeCounts()
    {
        var before = new WebAppSettings(Array.Empty<AppSettingValue>(), [new("Db", "Server=x", "SQLAzure", false)]);
        var after = new WebAppSettings(Array.Empty<AppSettingValue>(), [new("Db", "Server=x", "SQLServer", false)]);
        var plan = SettingsPlan.Create(before, after);
        Assert.True(plan.ConnectionStringsChanged);
        Assert.Equal(["Db"], plan.ChangedConnectionStrings);
    }

    [Fact]
    public void Plan_RenameCaseOnlyIsAChange()
    {
        var plan = SettingsPlan.Create(Settings(new AppSettingValue("name", "1", false)), Settings(new AppSettingValue("NAME", "1", false)));
        Assert.True(plan.AppSettingsChanged);
    }

    [Fact]
    public void MergeSlotNames_KeepsOtherSlotsNamesAndAppliesTicks()
    {
        var before = new List<AppSettingValue> { new("A", "1", true), new("B", "2", false), new("C", "3", true) };
        var after = new List<AppSettingValue> { new("A", "1", false), new("B", "2", true), new("D", "4", true) };
        // C was removed here: it keeps its tick because another slot may still have it.
        var merged = SettingsPlan.MergeSlotNames(["A", "C", "OTHER_SLOT_ONLY"], before, after, s => s.Name, s => s.SlotSetting);
        Assert.Equal(["C", "OTHER_SLOT_ONLY", "B", "D"], merged);
    }

    [Theory]
    [InlineData("", "needs a name")]
    [InlineData(" PADDED", "space")]
    [InlineData("A=B", "'='")]
    public void Validate_RejectsBadNames(string name, string expected)
    {
        var problem = SettingsPlan.Validate(Settings(new AppSettingValue(name, "v", false)));
        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void Validate_RejectsDuplicatesIgnoringCase()
    {
        var problem = SettingsPlan.Validate(Settings(new AppSettingValue("Key", "1", false), new("KEY", "2", false)));
        Assert.Contains("two settings", problem);
    }

    [Fact]
    public void Validate_RejectsUnknownConnectionStringType()
    {
        var problem = SettingsPlan.Validate(new WebAppSettings(Array.Empty<AppSettingValue>(), [new("Db", "x", "Oracle", false)]));
        Assert.Contains("unknown type", problem);
        Assert.Null(SettingsPlan.Validate(new WebAppSettings(Array.Empty<AppSettingValue>(), [new("Db", "x", "sqlazure", false)])));
    }

    [Theory]
    [InlineData("app", "Web App")]
    [InlineData("app,linux", "Web App (Linux)")]
    [InlineData("app,linux,container", "Web App (Linux, container)")]
    [InlineData("functionapp", "Function App")]
    [InlineData("functionapp,linux", "Function App (Linux)")]
    public void Kinds_DescribeLikeThePortal(string kind, string expected) => Assert.Equal(expected, WebAppKinds.Describe(kind));

    // ---------- the entry ----------

    [Fact]
    public void Entry_RoundTripsThroughJsonWithoutComputedProperties()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(new WebAppConnection { Name = "app-prod", ResourceId = SiteId, TenantId = "tenant-b", Tags = { "prod" } });

        var json = JsonSerializer.Serialize(doc, JsonDefaults.Options);
        Assert.Contains("\"type\": \"webapp\"", json);
        Assert.Contains("\"resourceId\"", json);
        Assert.DoesNotContain("\"kind\"", json);
        Assert.DoesNotContain("\"prefix\"", json);

        var loaded = JsonSerializer.Deserialize<ConnectionsDocument>(json, JsonDefaults.Options)!;
        var app = Assert.IsType<WebAppConnection>(Assert.Single(loaded.Connections));
        Assert.Equal(SiteId, app.ResourceId);
        Assert.Equal("tenant-b", app.TenantId);
        Assert.Equal(ConnectionKind.WebApp, app.Kind);
    }

    [Fact]
    public void Entry_CloneCopiesFieldsWithANewId()
    {
        var app = new WebAppConnection { Name = "a", ResourceId = SiteId, TenantId = "t", IconColour = "#ff0000" };
        var copy = Assert.IsType<WebAppConnection>(app.Clone());
        Assert.NotEqual(app.Id, copy.Id);
        Assert.Equal(SiteId, copy.ResourceId);
        Assert.Equal("t", copy.TenantId);
        Assert.Equal("#ff0000", copy.IconColour);
    }

    [Fact]
    public void Search_FindsWebAppsByResourceGroup()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(new WebAppConnection { Name = "Payments API", ResourceId = SiteId });
        Assert.True(ConnectionSearch.Matches(doc.Connections[0], "rg-web"));
    }
}
