using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.App.Services;

/// <summary>Windows the view models need to open. Implemented by the App so view models stay testable.</summary>
public interface IDialogService
{
    /// <summary>Edits the entry in place. Returns false when the user cancels (the entry is left untouched).</summary>
    /// <param name="knownVaults">Vault addresses offered when choosing a Key Vault credential.</param>
    /// <param name="inheritedColour">The icon colour the folders above pass down, so the editor can preview it.</param>
    bool EditEntry(ConnectionEntry entry, bool isNew, IReadOnlyList<string> knownVaults, string? inheritedColour = null);
    bool EditFolder(ConnectionFolder folder, bool isNew, IReadOnlyList<string> knownVaults, string? inheritedColour = null);
    bool EditSettings(AppSettings settings);
    /// <summary>The user's own watched services for an entry or folder. Null when cancelled.</summary>
    PersonalEntrySettings? EditPersonalWatchList(string targetName, IReadOnlyList<string> sharedList, PersonalEntrySettings current, bool followsUser);
    /// <summary>Folder permissions (SQL Server). True when something was saved.</summary>
    bool EditFolderPermissions(Data.SqlServer.SqlAdminService admin, ConnectionFolder folder);
    /// <summary>Users and groups (SQL Server, RCM admins).</summary>
    void ShowAdmin(Data.SqlServer.SqlAdminService admin, string? signedInAs);
    string? PickOpenFile(string title, string filter);
    string? PickSaveFile(string title, string filter, string fileName);
    /// <summary>Several files at once. Null when cancelled.</summary>
    IReadOnlyList<string>? PickOpenFiles(string title);
    /// <summary>A folder. Null when cancelled.</summary>
    string? PickFolder(string title);
    /// <summary>One line of text. <paramref name="validate"/> returns why the text is not allowed, or null. Null when cancelled.</summary>
    string? Prompt(string title, string message, string initial, Func<string, string?>? validate = null, string okLabel = "OK");
    /// <summary>Shows the vault item editor. True when the user saved and the view model validated.</summary>
    bool EditVaultItem(ViewModels.VaultItemEditorViewModel editor);
    /// <summary>Picks the folder to move <paramref name="itemName"/> into. Null when cancelled.</summary>
    MoveTarget? PickMoveTarget(string itemName, IReadOnlyList<MoveTarget> targets);
    bool Confirm(string message, string title);
    void Info(string message, string title);
    void Error(string message, string title);
}

/// <summary>
/// A place in the tree something could be moved to, in tree order. <see cref="Folder"/> null is the top level.
/// <see cref="Note"/> says why it cannot be picked (it is where the item is now, inside it, or not yours to change), or is null.
/// </summary>
public sealed record MoveTarget(ConnectionFolder? Folder, string Name, int Depth, string? Note)
{
    public bool Allowed => Note is null;
}
