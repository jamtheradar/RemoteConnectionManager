using System.Windows;
using Microsoft.Win32;
using RemoteConnectionManager.App.ViewModels;
using RemoteConnectionManager.App.Views;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Plugins;

namespace RemoteConnectionManager.App.Services;

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;
    private readonly PluginRegistry _plugins;

    public DialogService(PluginRegistry plugins)
    {
        _plugins = plugins;
    }

    private IReadOnlyCollection<CredentialSource> EnabledSources() => _plugins.CredentialProviders.Select(p => p.Source).ToList();

    public bool EditEntry(ConnectionEntry entry, bool isNew, IReadOnlyList<string> knownVaults, string? inheritedColour = null)
    {
        var vm = EntryEditorViewModel.ForEntry(entry, isNew, EnabledSources());
        vm.KnownVaults = knownVaults;
        vm.InheritedColour = inheritedColour;
        var window = new EntryEditorWindow { DataContext = vm, Owner = Owner };
        if (window.ShowDialog() != true) return false;
        vm.ApplyTo(entry);
        return true;
    }

    public bool EditFolder(ConnectionFolder folder, bool isNew, IReadOnlyList<string> knownVaults, string? inheritedColour = null)
    {
        var vm = EntryEditorViewModel.ForFolder(folder, isNew, EnabledSources());
        vm.KnownVaults = knownVaults;
        vm.InheritedColour = inheritedColour;
        var window = new EntryEditorWindow { DataContext = vm, Owner = Owner };
        if (window.ShowDialog() != true) return false;
        vm.ApplyTo(folder);
        return true;
    }

    public bool EditSettings(AppSettings settings)
    {
        var window = new SettingsWindow(settings, _plugins) { Owner = Owner };
        return window.ShowDialog() == true;
    }

    public PersonalEntrySettings? EditPersonalWatchList(string targetName, IReadOnlyList<string> sharedList, PersonalEntrySettings current, bool followsUser)
    {
        var window = new PersonalWatchListWindow(targetName, sharedList, current, followsUser) { Owner = Owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public bool EditFolderPermissions(Data.SqlServer.SqlAdminService admin, ConnectionFolder folder)
    {
        var window = new FolderPermissionsWindow(admin, folder) { Owner = Owner };
        return window.ShowDialog() == true;
    }

    public void ShowAdmin(Data.SqlServer.SqlAdminService admin, string? signedInAs)
    {
        var window = new AdminWindow(admin, signedInAs) { Owner = Owner };
        window.ShowDialog();
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string>? PickOpenFiles(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = "All files (*.*)|*.*", CheckFileExists = true, Multiselect = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? Prompt(string title, string message, string initial, Func<string, string?>? validate = null, string okLabel = "OK")
    {
        var window = new PromptWindow(title, message, initial, validate, okLabel) { Owner = Owner };
        return window.ShowDialog() == true ? window.Value : null;
    }

    public bool EditVaultItem(VaultItemEditorViewModel editor)
    {
        var window = new VaultItemEditorWindow { DataContext = editor, Owner = Owner };
        return window.ShowDialog() == true;
    }

    public MoveTarget? PickMoveTarget(string itemName, IReadOnlyList<MoveTarget> targets)
    {
        var window = new MoveToWindow(itemName, targets) { Owner = Owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public bool Confirm(string message, string title) =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Info(string message, string title) => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string message, string title) => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Owner;
        return owner is { IsLoaded: true }
            ? MessageBox.Show(owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
    }
}
