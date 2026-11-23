using Azure;
using RemoteConnectionManager.KeyVault;

namespace RemoteConnectionManager.AppService;

/// <summary>Turns SDK exceptions from Web App calls into one plain sentence for the in place error bar.</summary>
public static class AppServiceErrors
{
    public static string Describe(Exception ex, string action)
    {
        switch (ex)
        {
            case SettingsChangedElsewhereException:
                return "Someone changed this app's settings in Azure after you loaded them, so nothing was saved. Your edits are still shown here: note them, press Reload to get the latest, then make them again.";
            case InvalidOperationException io:
                return io.Message;
            case RequestFailedException rf:
                return DescribeRequest(rf, action);
            case HttpRequestException http:
                return $"{action} failed: could not reach Azure ({FirstLine(http.Message)}). Check your network.";
            default:
                // Sign in, cancellation and anything else read the same as for Key Vault.
                return KeyVaultErrors.Describe(ex, action);
        }
    }

    private static string DescribeRequest(RequestFailedException rf, string action)
    {
        var code = rf.ErrorCode ?? string.Empty;
        return rf.Status switch
        {
            401 => $"{action} failed: Azure did not accept the sign in. Sign out and sign in again.",
            403 when code.Equals("ScopeLocked", StringComparison.OrdinalIgnoreCase) =>
                $"{action} was blocked by a resource lock on this app or its resource group.",
            403 when code.Equals("RequestDisallowedByPolicy", StringComparison.OrdinalIgnoreCase) =>
                $"{action} was blocked by an Azure Policy assignment. {FirstLine(rf.Message)}",
            403 => $"{action} was denied. Your account needs a role such as Website Contributor or Contributor on this app (Reader only shows the overview).",
            404 => $"{action} failed: not found. The app or slot may have been deleted or renamed, or the resource ID is wrong.",
            409 => $"{action} failed: Azure reported a conflict, probably another change still running on this app. Wait a moment and try again.",
            429 => $"{action} was throttled by Azure. Wait a moment and try again.",
            _ => $"{action} failed ({rf.Status}{(code.Length > 0 ? " " + code : "")}): {FirstLine(rf.Message)}",
        };
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "..." : line;
    }
}
