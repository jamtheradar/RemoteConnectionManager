using Azure;
using RemoteConnectionManager.KeyVault;

namespace RemoteConnectionManager.Storage;

/// <summary>Turns SDK exceptions from storage calls into one plain sentence for the in place error bar.</summary>
public static class StorageErrors
{
    /// <summary>
    /// What to tell someone who has Owner or Contributor and still gets refused: management roles do not
    /// reach blob data when the app signs in with Entra ID.
    /// </summary>
    public const string DataRoleHint =
        "Your account needs a Storage Blob Data role on the account or container: Reader to browse and download, " +
        "Contributor to upload, rename and delete. Owner or Contributor on the subscription or account is not enough on its own.";

    public static string Describe(Exception ex, string action)
    {
        switch (ex)
        {
            case StorageTargetExistsException exists:
                return $"{action} stopped: '{exists.TargetName}' already exists. Nothing was replaced.";
            case InvalidOperationException io:
                return io.Message;
            case RequestFailedException rf:
                return DescribeRequest(rf, action);
            case HttpRequestException http:
                return $"{action} failed: could not reach the storage account ({FirstLine(http.Message)}). Check your network, or whether the account is only reachable through a private endpoint.";
            case IOException io:
                return $"{action} failed: {FirstLine(io.Message)}";
            case UnauthorizedAccessException ua:
                return $"{action} failed: Windows refused access to a local file. {FirstLine(ua.Message)}";
            default:
                // Sign in, cancellation and anything else read the same as for Key Vault.
                return KeyVaultErrors.Describe(ex, action);
        }
    }

    internal static string DescribeRequest(RequestFailedException rf, string action)
    {
        var code = rf.ErrorCode ?? string.Empty;
        bool Is(string c) => code.Equals(c, StringComparison.OrdinalIgnoreCase);
        return rf.Status switch
        {
            401 => $"{action} failed: the storage account did not accept the sign in. Sign out and sign in again.",
            403 when Is("AuthorizationPermissionMismatch") || Is("AuthorizationActionMismatch") =>
                $"{action} was denied. {DataRoleHint}",
            403 when Is("AuthorizationFailure") =>
                $"{action} was refused by the storage account. This usually means its firewall or private endpoint does not allow this PC's network. If the network is allowed, check your Storage Blob Data role.",
            403 when Is("AuthorizationSourceIPMismatch") =>
                $"{action} was refused: this PC's IP address is not in the storage account's firewall rules.",
            403 when Is("KeyBasedAuthenticationNotPermitted") =>
                $"{action} was refused: the account only allows Entra ID sign in.",
            403 when Is("ScopeLocked") => $"{action} was blocked by a resource lock on this account or its resource group.",
            403 when Is("RequestDisallowedByPolicy") => $"{action} was blocked by an Azure Policy assignment. {FirstLine(rf.Message)}",
            403 => $"{action} was denied ({(code.Length > 0 ? code : "403")}). {DataRoleHint}",
            404 when Is("ContainerNotFound") || Is("FilesystemNotFound") => $"{action} failed: the container no longer exists.",
            404 when Is("BlobNotFound") || Is("PathNotFound") => $"{action} failed: it no longer exists. Someone may have deleted or renamed it; press Refresh.",
            404 when Is("ResourceNotFound") => $"{action} failed: the storage account was not found. It may have been deleted, or the resource ID is wrong.",
            404 => $"{action} failed: not found.",
            409 when Is("ContainerAlreadyExists") => $"{action} failed: a container with that name already exists.",
            409 when Is("ContainerBeingDeleted") => $"{action} failed: a container with that name is still being deleted. Wait a minute and try again.",
            409 when Is("BlobAlreadyExists") || Is("PathAlreadyExists") => $"{action} stopped: something with that name already exists. Nothing was replaced.",
            409 when Is("BlobArchived") => $"{action} failed: the blob is in the Archive tier and must be rehydrated in the Azure portal before it can be read.",
            409 when Is("BlobImmutableDueToPolicy") || Is("BlobImmutableDueToLegalHold") =>
                $"{action} failed: an immutability policy or legal hold protects this blob.",
            409 when Is("LeaseIdMissing") || Is("LeaseAlreadyPresent") => $"{action} failed: the blob or container has an active lease (something else is using it).",
            409 when Is("DirectoryNotEmpty") => $"{action} failed: the folder is not empty.",
            412 when Is("SourceConditionNotMet") => $"{action} stopped: the blob changed while it was being copied. Press Refresh and try again.",
            412 when Is("ConditionNotMet") => $"{action} stopped: something with that name already exists, or it changed meanwhile. Nothing was replaced.",
            412 when Is("LeaseIdMissing") => $"{action} failed: the blob or container has an active lease (something else is using it).",
            429 or 503 => $"{action} was throttled by Azure Storage. Wait a moment and try again.",
            _ => $"{action} failed ({rf.Status}{(code.Length > 0 ? " " + code : "")}): {FirstLine(rf.Message)}",
        };
    }

    /// <summary>True for a refusal that a Storage Blob Data role would fix, so the view can show the hint.</summary>
    public static bool IsMissingDataRole(Exception ex) =>
        ex is RequestFailedException { Status: 403 } rf
        && (rf.ErrorCode ?? string.Empty) is "AuthorizationPermissionMismatch" or "AuthorizationActionMismatch";

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "..." : line;
    }
}
