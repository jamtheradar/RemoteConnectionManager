using System.Text.Json;
using Azure;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.Storage;
using RemoteConnectionManager.Storage;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class StorageTests
{
    private const string Sub = "0b1f6471-1bf0-4dda-aec3-111122223333";
    private const string AccountId = "/subscriptions/" + Sub + "/resourceGroups/rg-data/providers/Microsoft.Storage/storageAccounts/stprodlogs";

    // ---------- resource IDs ----------

    [Fact]
    public void Parse_ReadsAccountResourceId()
    {
        var id = StorageAccountId.Parse(AccountId);
        Assert.NotNull(id);
        Assert.Equal(Sub, id!.SubscriptionId);
        Assert.Equal("rg-data", id.ResourceGroup);
        Assert.Equal("stprodlogs", id.AccountName);
        Assert.Equal(AccountId, id.ToString());
        Assert.Equal(new Uri("https://stprodlogs.blob.core.windows.net/"), id.DefaultBlobEndpoint);
    }

    [Theory]
    [InlineData("https://portal.azure.com/#@contoso.onmicrosoft.com/resource" + AccountId + "/overview")]
    [InlineData("https://portal.azure.com/#view/resource" + AccountId + "/containersList?x=1")]
    [InlineData("subscriptions/" + Sub + "/resourceGroups/rg-data/providers/Microsoft.Storage/storageAccounts/stprodlogs")]
    [InlineData("  " + AccountId + "  ")]
    public void Parse_ReadsPortalAddressesAndLooseForms(string text)
    {
        Assert.Equal(AccountId, StorageAccountId.Parse(text)?.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stprodlogs")]
    [InlineData("/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Web/sites/app")]
    [InlineData("/subscriptions/not-a-guid/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/stprodlogs")]
    [InlineData("/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/st-bad-name")]
    [InlineData("/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/ab")]
    public void Parse_RejectsOtherResources(string? text)
    {
        Assert.Null(StorageAccountId.Parse(text));
    }

    [Fact]
    public void Parse_LowerCasesTheAccountName()
    {
        Assert.Equal("stprodlogs", StorageAccountId.Parse(AccountId.Replace("stprodlogs", "StProdLogs"))!.AccountName);
    }

    [Fact]
    public void PortalUrl_KeepsTheTenant()
    {
        var id = StorageAccountId.Parse(AccountId)!;
        Assert.Equal($"https://portal.azure.com/#@contoso.com/resource{AccountId}/overview", id.PortalUrl("contoso.com"));
        Assert.Equal($"https://portal.azure.com/#/resource{AccountId}/overview", id.PortalUrl(null));
    }

    // ---------- prefixes and names ----------

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("a", "a/")]
    [InlineData("/a/b/", "a/b/")]
    [InlineData("a/b", "a/b/")]
    public void NormalisePrefix_HasOneTrailingSlash(string? input, string expected)
    {
        Assert.Equal(expected, BlobPaths.NormalisePrefix(input));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a/", "")]
    [InlineData("a/b/", "a/")]
    [InlineData("a/b/c/", "a/b/")]
    public void Parent_GoesUpOneLevel(string prefix, string expected)
    {
        Assert.Equal(expected, BlobPaths.Parent(prefix));
    }

    [Theory]
    [InlineData("a/b/c.txt", "a/b/")]
    [InlineData("c.txt", "")]
    [InlineData("a/b", "a/")]
    [InlineData("a/b/", "a/")]
    public void ContainingPrefix_IsTheFolderAbove(string name, string expected)
    {
        Assert.Equal(expected, BlobPaths.ContainingPrefix(name));
    }

    [Theory]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("a/b/", "b")]
    [InlineData("c.txt", "c.txt")]
    public void LeafName_IsTheLastSegment(string name, string expected)
    {
        Assert.Equal(expected, BlobPaths.LeafName(name));
    }

    [Fact]
    public void Breadcrumb_ListsEachLevel()
    {
        var crumbs = BlobPaths.Breadcrumb("logs/2026/11/");
        Assert.Equal([("logs", "logs/"), ("2026", "logs/2026/"), ("11", "logs/2026/11/")], crumbs);
        Assert.Empty(BlobPaths.Breadcrumb(""));
    }

    [Theory]
    [InlineData("logs", true)]
    [InlineData("app-data-01", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    [InlineData("Logs", false)]
    [InlineData("-logs", false)]
    [InlineData("logs-", false)]
    [InlineData("log--s", false)]
    [InlineData("log_s", false)]
    [InlineData("", false)]
    public void ValidateContainerName_FollowsAzureRules(string name, bool ok)
    {
        Assert.Equal(ok, BlobPaths.ValidateContainerName(name) is null);
        Assert.Null(BlobPaths.ValidateContainerName(new string('a', 63)));
        Assert.NotNull(BlobPaths.ValidateContainerName(new string('a', 64)));
    }

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("2026 Q4", true)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("..", false)]
    [InlineData("name.", false)]
    [InlineData(" padded", false)]
    [InlineData("", false)]
    public void ValidateLeafName_StopsNamesThatBreakFolders(string name, bool ok)
    {
        Assert.Equal(ok, BlobPaths.ValidateLeafName(name) is null);
    }

    [Theory]
    [InlineData("a/b/c.txt", true)]
    [InlineData("a//c.txt", false)]
    [InlineData("a/", false)]
    [InlineData("", false)]
    public void ValidateBlobName_ChecksTheWholeName(string name, bool ok)
    {
        Assert.Equal(ok, BlobPaths.ValidateBlobName(name) is null);
        Assert.NotNull(BlobPaths.ValidateBlobName(new string('a', 1025)));
    }

    [Fact]
    public void Retarget_MovesAFolderPrefix()
    {
        Assert.Equal("new/x/y.txt", BlobPaths.Retarget("old/x/y.txt", "old/", "new"));
        Assert.Equal("a/renamed/f.bin", BlobPaths.Retarget("a/b/f.bin", "a/b", "a/renamed/"));
        Assert.Throws<ArgumentException>(() => BlobPaths.Retarget("other/f.bin", "a/b/", "a/c/"));
    }

    // ---------- listing ----------

    [Fact]
    public void BuildListing_PutsFoldersFirstAndTurnsDataLakeMarkersIntoFolders()
    {
        var listing = BlobPaths.BuildListing(
            ["logs/b/", "logs/a/"],
            [
                (new BlobEntry { Name = "logs/z.txt", Size = 5 }, false),
                (new BlobEntry { Name = "logs/a", Size = 0 }, true),       // directory with contents: also a prefix
                (new BlobEntry { Name = "logs/empty", Size = 0 }, true),   // empty directory: only a marker
                (new BlobEntry { Name = "logs/A.txt", Size = 1 }, false),
            ]);

        Assert.Equal(["logs/a", "logs/b", "logs/empty", "logs/A.txt", "logs/z.txt"], listing.Select(e => e.Name));
        Assert.All(listing.Take(3), e => Assert.True(e.IsFolder));
        Assert.Equal("logs/a/", listing[0].FolderPrefix);
        Assert.Equal("a", listing[0].DisplayName);
    }

    [Fact]
    public void PlanUpload_KeepsFolderTreesAndSkipsHiddenFiles()
    {
        var root = Directory.CreateTempSubdirectory("rcm-storage-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "single.txt"), "hello");
            var site = Directory.CreateDirectory(Path.Combine(root.FullName, "site"));
            Directory.CreateDirectory(Path.Combine(site.FullName, "css"));
            File.WriteAllText(Path.Combine(site.FullName, "index.html"), "<html/>");
            File.WriteAllText(Path.Combine(site.FullName, "css", "app.css"), "body{}");
            var hidden = Path.Combine(site.FullName, "secret.ini");
            File.WriteAllText(hidden, "x");
            File.SetAttributes(hidden, FileAttributes.Hidden);
            Directory.CreateDirectory(Path.Combine(site.FullName, "empty"));

            var plan = BlobPaths.PlanUpload([Path.Combine(root.FullName, "single.txt"), site.FullName, Path.Combine(root.FullName, "missing")], "web/");

            Assert.Equal(
                ["web/single.txt", "web/site/css/app.css", "web/site/index.html"],
                plan.Select(p => p.BlobName).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(5, plan.Single(p => p.BlobName == "web/single.txt").Size);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void LocalPathFor_KeepsTheFolderAndStaysInsideTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rcm-download");
        Assert.Equal(Path.Combine(root, "logs", "2026", "a.txt"), BlobPaths.LocalPathFor(root, "data/logs/2026/a.txt", "data/"));
        Assert.Equal(Path.Combine(root, "a.txt"), BlobPaths.LocalPathFor(root, "a.txt", ""));
    }

    [Theory]
    [InlineData("data/../../evil.txt")]
    [InlineData("data/./x")]
    [InlineData("other/a.txt")]
    public void LocalPathFor_RefusesNamesThatEscapeOrDoNotBelong(string name)
    {
        Assert.Null(BlobPaths.LocalPathFor(Path.Combine(Path.GetTempPath(), "rcm-download"), name, "data/"));
    }

    [Fact]
    public void LocalPathFor_CleansCharactersWindowsDoesNotAllow()
    {
        var root = Path.Combine(Path.GetTempPath(), "rcm-download");
        Assert.Equal(Path.Combine(root, "a_b_c.txt"), BlobPaths.LocalPathFor(root, "a:b?c.txt", ""));
        Assert.Equal(Path.Combine(root, "_CON.txt"), BlobPaths.LocalPathFor(root, "CON.txt", ""));
        Assert.Equal(Path.Combine(root, "trailing"), BlobPaths.LocalPathFor(root, "trailing. ", ""));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 GB")]
    public void FormatSize_UsesBinaryUnits(long? bytes, string expected)
    {
        Assert.Equal(expected, BlobPaths.FormatSize(bytes));
    }

    [Theory]
    [InlineData("index.HTML", "text/html")]
    [InlineData("data.json", "application/json")]
    [InlineData("backup.bak", "application/octet-stream")]
    [InlineData("noextension", "application/octet-stream")]
    public void ContentTypeFor_UsesTheExtension(string file, string expected)
    {
        Assert.Equal(expected, BlobPaths.ContentTypeFor(file));
    }

    // ---------- errors ----------

    [Fact]
    public void Errors_ExplainTheMissingDataRole()
    {
        var ex = new RequestFailedException(403, "This request is not authorized to perform this operation using this permission.", "AuthorizationPermissionMismatch", null);
        var text = StorageErrors.Describe(ex, "Listing containers");
        Assert.StartsWith("Listing containers was denied.", text);
        Assert.Contains("Storage Blob Data", text);
        Assert.Contains("not enough", text);
        Assert.True(StorageErrors.IsMissingDataRole(ex));
    }

    [Fact]
    public void Errors_PointAtTheFirewallForAuthorizationFailure()
    {
        var ex = new RequestFailedException(403, "This request is not authorized to perform this operation.", "AuthorizationFailure", null);
        Assert.Contains("firewall", StorageErrors.Describe(ex, "Listing containers"));
        Assert.False(StorageErrors.IsMissingDataRole(ex));
    }

    [Theory]
    [InlineData(409, "BlobArchived", "Archive tier")]
    [InlineData(409, "ContainerAlreadyExists", "already exists")]
    [InlineData(404, "ContainerNotFound", "container no longer exists")]
    [InlineData(412, "ConditionNotMet", "Nothing was replaced")]
    public void Errors_ReadTheStorageErrorCodes(int status, string code, string expected)
    {
        Assert.Contains(expected, StorageErrors.Describe(new RequestFailedException(status, "x", code, null), "Doing it"));
    }

    [Fact]
    public void Errors_ExistingTargetSaysNothingWasReplaced()
    {
        Assert.Equal("Renaming stopped: 'b.txt' already exists. Nothing was replaced.", StorageErrors.Describe(new StorageTargetExistsException("b.txt"), "Renaming"));
    }

    [Fact]
    public void SoftDeleteSentence_SaysWhetherDeleteCanBeUndone()
    {
        StorageAccountOverview Make(bool known, int? days) => new()
        {
            Name = "st", ResourceId = AccountId, ResourceGroup = "rg", SubscriptionId = Sub,
            BlobEndpoint = new Uri("https://st.blob.core.windows.net/"), SoftDeleteKnown = known, BlobSoftDeleteDays = days,
        };
        Assert.Contains("14 days", Make(true, 14).SoftDeleteSentence);
        Assert.Contains("OFF", Make(true, null).SoftDeleteSentence);
        Assert.Contains("not known", Make(false, null).SoftDeleteSentence);
    }

    // ---------- the entry ----------

    [Fact]
    public void Entry_RoundTripsThroughJsonWithoutComputedProperties()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(new StorageAccountConnection { Name = "Prod logs", ResourceId = AccountId, TenantId = "tenant-b", Container = "logs" });

        var json = JsonSerializer.Serialize(doc, JsonDefaults.Options);
        Assert.Contains("\"type\": \"storage\"", json);
        Assert.Contains("\"container\": \"logs\"", json);
        Assert.DoesNotContain("\"kind\"", json);
        Assert.DoesNotContain("\"prefix\"", json);

        var loaded = JsonSerializer.Deserialize<ConnectionsDocument>(json, JsonDefaults.Options)!;
        var entry = Assert.IsType<StorageAccountConnection>(Assert.Single(loaded.Connections));
        Assert.Equal(AccountId, entry.ResourceId);
        Assert.Equal("tenant-b", entry.TenantId);
        Assert.Equal("logs", entry.Container);
        Assert.Equal(ConnectionKind.StorageAccount, entry.Kind);
        Assert.Equal("(STG)", entry.Prefix);
    }

    [Fact]
    public void Entry_CloneCopiesFieldsWithANewId()
    {
        var entry = new StorageAccountConnection { Name = "a", ResourceId = AccountId, TenantId = "t", Container = "c", IconColour = "#ff0000" };
        var copy = Assert.IsType<StorageAccountConnection>(entry.Clone());
        Assert.NotEqual(entry.Id, copy.Id);
        Assert.Equal(AccountId, copy.ResourceId);
        Assert.Equal("t", copy.TenantId);
        Assert.Equal("c", copy.Container);
        Assert.Equal("#ff0000", copy.IconColour);
    }

    [Fact]
    public void Search_FindsStorageByAccountAndContainer()
    {
        var entry = new StorageAccountConnection { Name = "Logs", ResourceId = AccountId, Container = "nightly-backups" };
        Assert.True(ConnectionSearch.Matches(entry, "stprodlogs"));
        Assert.True(ConnectionSearch.Matches(entry, "nightly"));
    }
}
