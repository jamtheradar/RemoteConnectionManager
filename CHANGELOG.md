# Changelog

All notable changes to Remote Connection Manager are listed here. Each release on the
[Releases](https://github.com/jamtheradar/RemoteConnectionManager/releases) page has an installer.

## [Unreleased]

### Added
- Move entries and folders to another folder or the top level:
  - Drag them in the navigation tree. Dropping on a folder moves into it; dropping on an entry moves into that entry's folder; dropping on empty space moves to the top level. Closed folders open if you hover on them, and the tree scrolls near its edges. Moving a folder asks first.
  - Or right click > Move to... (Ctrl+M in the tree) and pick a folder from a list you can filter. Places you cannot move to are greyed with the reason.
  - In the shared database, moving an entry needs Edit on both folders, and moving a folder needs Manage on it and Edit on where it goes. A moved folder keeps its own permissions but now inherits from its new parent, so the tree reloads afterwards.
- Azure Web App entries (APP) for Web Apps and Function Apps. Opening one shows a tab like the portal's Overview page:
  - Status, default domain, App Service plan and tier, runtime stack, OS, region, HTTPS only, TLS, managed identity, outbound IPs and more.
  - Browse, Start, Stop and Restart (Stop and Restart ask first), Open in portal and Copy resource ID.
  - A slot picker for deployment slots. Everything in the tab applies to the slot shown.
  - Environment variables (app settings) and connection strings, with values hidden until you show them. You can add, edit and remove them, and set the deployment slot setting tick. Copy uses the self-clearing clipboard. Key Vault references are flagged and open the vault.
  - Save lists the changes by name, warns that it restarts the app, and checks that nobody changed the settings in Azure since you loaded them.
- Azure Storage account entries (STG) with a blob browser, in the spirit of Azure Storage Explorer:
  - Containers on the left; create one, or delete one by typing its name.
  - A folder style listing with a breadcrumb, Up (Backspace), a name filter and Load more for very large folders.
  - Properties and metadata of the selected blob.
  - Upload files or whole folders, from buttons or by dragging them from Explorer. If files already exist you choose to replace or skip them.
  - Download files or folders (folders keep their tree). Blob names Windows cannot use are cleaned, and names that would land outside the chosen folder are skipped.
  - New folder, Rename (F2) and Delete (Delete key, asks first and says whether soft delete can get the blob back).
  - Accounts with a hierarchical namespace (Data Lake Gen2) get real folders and one-step rename and delete. Elsewhere a rename copies on the server and then deletes the original, and a folder rename only deletes the originals if every copy worked.
  - Transfers run in a panel at the bottom with progress and Cancel, so you can keep browsing.
  - An Overview with replication, namespace, endpoints, soft delete, versioning, firewall and key access settings.
  - Signs in with Entra ID only, never account keys or SAS. Your account needs a Storage Blob Data role (Reader to browse and download, Contributor to change things); refusals say so.
- The Azure browser (Ctrl+Shift+K) now lists Web Apps and storage accounts next to Key Vaults, so you can open them or pin them to the tree.

## [0.3.0] - 2026-11-11

### Added
- Silent all users install for MDM tools such as Intune: `setup.exe /VERYSILENT /ALLUSERS /SUPPRESSMSGBOXES /NORESTART` installs to `C:\Program Files\RemoteConnectionManager`. The README lists the uninstall command and detection rule.
- Default settings for new users. A `default-settings.json` in `C:\ProgramData\RemoteConnectionManager` is copied to a user's settings the first time they run RCM, for example to point everyone at the shared SQL database.
  - The installer puts it there from a `default-settings.json` next to the setup exe, or from `/DEFAULTSETTINGS=<file>`, on all users installs.
- Environment variables such as `%OneDrive%` now work in the connections file path.

### Changed
- A silent install no longer asks about a missing .NET 10 Desktop Runtime. It installs anyway and notes it in the setup log.
- Setup run as SYSTEM without `/ALLUSERS` now stops with exit code 1, rather than installing into the SYSTEM profile where no one can use it.

## [0.2.0] - 2026-11-06

### Added
- Password generator. Open it from the command bar (key icon) or with Ctrl+Shift+G, and it copies the password to the clipboard.
  - Types: random, memorable (words) and PIN, with a length slider.
  - Random passwords can include numbers and symbols, and always have at least one of each type chosen.
  - Memorable passwords can use a separator, capitals and a number.
  - A bar shows how strong the password is.
  - Passwords come from the Windows crypto random number source.
  - The clipboard copy stays out of clipboard history and cloud clipboard, and is cleared after 30 seconds.
  - Your last choices are saved. Generated passwords are never saved.
- Generate button in the Key Vault item editor. It puts the password straight into the Value field.
- "Ignore certificate errors" RDP option. It connects without the "identity of the remote computer cannot be verified" prompt.
  - Set it on a connection, a folder or as a document default. Connections and subfolders inherit it.
  - It is shown on the dashboard with the other RDP settings.
  - The mRemoteNG import brings it across from `RDPAuthenticationLevel="NoAuth"`.

### Fixed
- Text boxes across the app had double padding, which pushed text off centre in search and filter boxes.
- The Key Vault tab no longer shows "Nothing matches the filter" over the list while items are loading or showing.

## [0.1.0] - 2026-11-06

The first release.

### Added
- **Connections**
  - Folders with settings inherited from parent folders and the document defaults.
  - A navigation tree with a filter, and inherited icon colours.
  - Quick connect from the status bar.
- **Import** from mRemoteNG (`confCons.xml`), Devolutions RDM (`.rdm` or RDP `.csv`) and Royal TS (`.rtsz`). Passwords are not imported.
- **Storage**
  - A local JSON file that reloads when it changes on disk.
  - Or a shared SQL Server or Azure SQL database, signed in with Windows or Microsoft Entra.
  - In the database, permissions are managed in RCM and enforced by the database. Preferences are kept per person.
  - Import and export between the two in JSON.
- **Remote Desktop**
  - Sessions in tabs that resize with the window, with keyboard pass through and clipboard.
  - Passwords from Azure Key Vault or 1Password, read when the session connects. They are never saved.
  - RD Gateway, Restricted Admin and Remote Credential Guard.
  - Move a session to its own window, or to full screen on any display.
- **Terminals**
  - PowerShell 7, Windows PowerShell and cmd tabs, with start scripts. Ctrl+T opens a quick shell.
  - SSH through Windows' `ssh.exe`, with an option for older algorithms.
  - A built in telnet client.
  - Send password and Send user name from Key Vault or 1Password.
- **Websites** open in the default browser or a WebView2 tab.
- **Session logs** for PowerShell, cmd, SSH and telnet tabs.
  - Plain text, on by default and kept for 90 days.
  - Passwords, secrets and keys that RCM recognises are masked.
- **Azure Key Vault browser**
  - Signs in through the Windows account manager.
  - Browse vaults or pin them to the tree.
  - Manage secrets, keys and certificates.
  - Copied values are cleared after 30 seconds.
- **Plugins**
  - Key Vault, 1Password and Server checks, each switched on or off in Settings.
  - Server checks show services and hardware stats for a host. They read only and never change the host.
- **Installer**
  - Installs for the current user or for all users.
  - Needs the .NET 10 Desktop Runtime (x64).

[Unreleased]: https://github.com/jamtheradar/RemoteConnectionManager/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/jamtheradar/RemoteConnectionManager/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/jamtheradar/RemoteConnectionManager/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/jamtheradar/RemoteConnectionManager/releases/tag/v0.1.0
