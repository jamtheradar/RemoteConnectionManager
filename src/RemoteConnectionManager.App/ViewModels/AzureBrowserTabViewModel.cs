using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.AppService;
using RemoteConnectionManager.KeyVault;
using RemoteConnectionManager.Storage;

namespace RemoteConnectionManager.App.ViewModels;

public enum AzureNodeKind
{
    Subscription,
    ResourceGroup,
    Vault,
    WebApp,
    StorageAccount,
    Message,
}

/// <summary>A row in the Azure browser tree. Property names match the shared TreeViewItem style.</summary>
public sealed partial class AzureNodeViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _detail = string.Empty;

    public AzureNodeKind Kind { get; }
    public VaultInfo? Vault { get; init; }
    public WebAppInfo? WebApp { get; init; }
    public StorageAccountInfo? StorageAccount { get; init; }
    public SubscriptionInfo? Subscription { get; init; }
    public ObservableCollection<AzureNodeViewModel> Children { get; } = new();

    public bool IsVault => Kind == AzureNodeKind.Vault;
    /// <summary>A vault, Web App or storage account: something Open and Pin work on.</summary>
    public bool IsOpenable => Kind is AzureNodeKind.Vault or AzureNodeKind.WebApp or AzureNodeKind.StorageAccount;
    public string Glyph => Kind switch
    {
        AzureNodeKind.Subscription => "",
        AzureNodeKind.ResourceGroup => "",
        AzureNodeKind.Vault => "",
        AzureNodeKind.WebApp => "\uE753",
        AzureNodeKind.StorageAccount => "\uE7B8",
        _ => "",
    };

    /// <summary>Theme brush key for the glyph colour (Icon.* tokens in Themes/Rcm.xaml).</summary>
    public string IconBrushKey => Kind switch
    {
        AzureNodeKind.Subscription => "Icon.Subscription",
        AzureNodeKind.ResourceGroup => "Icon.ResourceGroup",
        AzureNodeKind.Vault => "Icon.Vault",
        AzureNodeKind.WebApp => "Icon.WebApp",
        AzureNodeKind.StorageAccount => "Icon.Storage",
        _ => "Icon.Default",
    };

    public AzureNodeViewModel(AzureNodeKind kind, string displayName)
    {
        Kind = kind;
        _displayName = displayName;
    }

    public bool ApplyFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            IsVisible = true;
            foreach (var child in Children) child.ApplyFilter(filter);
            return true;
        }
        var self = DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var any = false;
        foreach (var child in Children) any |= child.ApplyFilter(filter);
        if (self && !any)
            foreach (var child in Children) child.ShowAll();
        IsVisible = self || any;
        if (any) IsExpanded = true;
        return IsVisible;
    }

    private void ShowAll()
    {
        IsVisible = true;
        foreach (var child in Children) child.ShowAll();
    }

    public IEnumerable<AzureNodeViewModel> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var grand in child.Descendants()) yield return grand;
        }
    }
}

/// <summary>
/// The "Azure" tab: sign in, list every Key Vault and Web App (or Function App) the account can see
/// through Azure Resource Manager, open one or pin it to the connections tree. Opening by name works without
/// any ARM permission, for accounts that only have data plane roles on a vault.
/// </summary>
public sealed partial class AzureBrowserTabViewModel : TabViewModelBase, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly AzureAuth _auth;
    private readonly VaultCatalog _catalog;
    private readonly WebAppCatalog _webApps;
    private readonly StorageCatalog _storage;
    private readonly AppLog _log;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _openByName = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = string.Empty;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _errorNeedsSignIn;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private AzureNodeViewModel? _selectedNode;

    public ObservableCollection<AzureNodeViewModel> Roots { get; } = new();

    public bool IsSignedIn => _auth.IsSignedIn;
    public bool NeedsSignIn => _auth.IsSignedIn && _auth.NeedsSignIn;
    /// <summary>The accent Sign in button: shown when signed out or when the saved sign in has expired.</summary>
    public bool ShowSignIn => !_auth.IsSignedIn || _auth.NeedsSignIn;
    public string SignInLabel => NeedsSignIn ? "Sign in again" : "Sign in";
    public string AccountText => _auth.AccountName is not { } name ? "Not signed in to Azure"
        : _auth.IsPrompting ? $"Signing in as {name}: waiting for the Microsoft sign in window"
        : _auth.NeedsSignIn ? $"{name}: sign in expired. Sign in again to keep using Azure."
        : $"Signed in as {name}";
    public bool HasError => ErrorText.Length > 0;
    public bool ShowBusyOverlay => IsBusy && Roots.Count == 0;
    public bool CanUseSelected => SelectedNode?.IsOpenable == true;
    public string OpenLabel => SelectedNode?.Kind switch
    {
        AzureNodeKind.WebApp => "Open app",
        AzureNodeKind.StorageAccount => "Open storage",
        _ => "Open vault",
    };

    public AzureBrowserTabViewModel(ShellViewModel shell, AzureAuth auth, VaultCatalog catalog, WebAppCatalog webApps, StorageCatalog storage, AppLog log)
    {
        _shell = shell;
        _auth = auth;
        _catalog = catalog;
        _webApps = webApps;
        _storage = storage;
        _log = log;
        Title = "Azure";
        Glyph = "";
        IconBrushKey = "Icon.Azure";
        _auth.Changed += OnAuthChanged;
        Roots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowBusyOverlay));
        SummaryText = _auth.IsSignedIn ? "Press Refresh to list the vaults, Web Apps and storage accounts your account can see." : "Sign in to list the vaults, Web Apps and storage accounts your account can see.";
        if (_auth.IsSignedIn) _ = LoadAsync();
    }

    private void OnAuthChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnAuthChanged(sender, e));
            return;
        }
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(NeedsSignIn));
        OnPropertyChanged(nameof(ShowSignIn));
        OnPropertyChanged(nameof(SignInLabel));
        OnPropertyChanged(nameof(AccountText));
        if (IsBusy && _auth.IsPrompting)
            BusyText = "Waiting for you to sign in to Azure. Look for the Microsoft sign in window, it may be behind this one...";
    }

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowBusyOverlay));

    partial void OnFilterTextChanged(string value)
    {
        foreach (var root in Roots) root.ApplyFilter(value);
    }

    partial void OnSelectedNodeChanged(AzureNodeViewModel? value)
    {
        OnPropertyChanged(nameof(CanUseSelected));
        OnPropertyChanged(nameof(OpenLabel));
        OpenSelectedCommand.NotifyCanExecuteChanged();
        PinSelectedCommand.NotifyCanExecuteChanged();
    }

    // ---------- sign in ----------

    [RelayCommand]
    private async Task SignIn()
    {
        var cts = NewOperation();
        if (await RunAsync("Signing in", "Signing in", ct => _auth.EnsureSignedInAsync(null, AzureAuth.ArmScope, ct), cts.Token))
            await LoadAsync();
    }

    [RelayCommand]
    private async Task SignInAnother()
    {
        var cts = NewOperation();
        if (await RunAsync("Signing in", "Signing in", ct => _auth.SignInWithAnotherAccountAsync(ct), cts.Token))
            await LoadAsync();
    }

    [RelayCommand]
    private void SignOut()
    {
        StopOperation();
        _auth.SignOut();
        Roots.Clear();
        ErrorText = string.Empty;
        ErrorNeedsSignIn = false;
        SummaryText = "Signed out. Open vault tabs stop working until you sign in again.";
        _log.Info("Signed out of Azure");
    }

    /// <summary>Stops a sign in or listing that is taking too long, for example a hidden sign in window.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!IsBusy) return;
        StopOperation();
        SummaryText = Roots.Count > 0 ? "Cancelled. The list may be incomplete." : "Cancelled.";
    }

    private CancellationTokenSource NewOperation()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        return cts;
    }

    private void StopOperation()
    {
        _loadCts?.Cancel();
        _loadCts = null;
        IsBusy = false;
        BusyText = string.Empty;
    }

    // ---------- listing ----------

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        var cts = NewOperation();

        IReadOnlyList<SubscriptionInfo> subscriptions = Array.Empty<SubscriptionInfo>();
        var ok = await RunAsync("Listing subscriptions", "Listing subscriptions", async ct =>
        {
            BusyText = "Checking Azure sign in...";
            await _auth.EnsureSignedInAsync(null, AzureAuth.ArmScope, ct);
            BusyText = "Listing subscriptions...";
            subscriptions = await _catalog.GetSubscriptionsAsync(ct);
        }, cts.Token);
        if (!ok || cts.IsCancellationRequested) return;

        Roots.Clear();
        var vaultCount = 0;
        var appCount = 0;
        var storageCount = 0;
        var done = 0;
        BusyText = $"Looking for vaults, Web Apps and storage in {subscriptions.Count} subscriptions...";
        IsBusy = true;
        using var gate = new SemaphoreSlim(4);

        var tasks = subscriptions.Select(async sub =>
        {
            await gate.WaitAsync(cts.Token);
            try
            {
                // Vaults and apps are listed separately, so one failing still shows the other.
                var errors = new List<string>();
                var vaultTask = _catalog.GetVaultsAsync(sub, cts.Token);
                var appTask = _webApps.GetWebAppsAsync(sub, cts.Token);
                var storageTask = _storage.GetAccountsAsync(sub, cts.Token);
                IReadOnlyList<VaultInfo> vaults = Array.Empty<VaultInfo>();
                IReadOnlyList<WebAppInfo> apps = Array.Empty<WebAppInfo>();
                IReadOnlyList<StorageAccountInfo> accounts = Array.Empty<StorageAccountInfo>();
                try { vaults = await vaultTask; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(KeyVaultErrors.Describe(ex, "Listing vaults"));
                    _log.Warn($"Listing vaults in {sub.Name} failed: {ex.GetType().Name}: {FirstLine(ex.Message)}");
                }
                try { apps = await appTask; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(AppServiceErrors.Describe(ex, "Listing Web Apps"));
                    _log.Warn($"Listing Web Apps in {sub.Name} failed: {ex.GetType().Name}: {FirstLine(ex.Message)}");
                }
                try { accounts = await storageTask; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(StorageErrors.Describe(ex, "Listing storage accounts"));
                    _log.Warn($"Listing storage accounts in {sub.Name} failed: {ex.GetType().Name}: {FirstLine(ex.Message)}");
                }
                if (cts.IsCancellationRequested || (vaults.Count == 0 && apps.Count == 0 && accounts.Count == 0 && errors.Count == 0)) return;
                vaultCount += vaults.Count;
                appCount += apps.Count;
                storageCount += accounts.Count;
                AddSubscription(sub, vaults, apps, accounts, errors);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            finally
            {
                gate.Release();
                done++;
                if (!cts.IsCancellationRequested) BusyText = $"Looking for vaults... {done} of {subscriptions.Count} subscriptions";
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        if (cts.IsCancellationRequested) return;

        IsBusy = false;
        BusyText = string.Empty;
        SummaryText = $"{vaultCount} vaults, {appCount} Web Apps and {storageCount} storage accounts in {Roots.Count(r => r.Children.Any(c => c.Kind != AzureNodeKind.Message))} of {subscriptions.Count} subscriptions. Subscriptions with none are hidden."
            + $"  ·  refreshed {DateTime.Now:HH:mm:ss}";
        if (!string.IsNullOrWhiteSpace(FilterText))
            foreach (var root in Roots) root.ApplyFilter(FilterText);
        if (Roots.Count == 1) Roots[0].IsExpanded = true;
        _log.Info($"Azure browser found {vaultCount} vaults, {appCount} Web Apps and {storageCount} storage accounts in {subscriptions.Count} subscriptions");
    }

    private static string FirstLine(string message) => message.Split('\n')[0];

    private void AddSubscription(SubscriptionInfo sub, IReadOnlyList<VaultInfo> vaults, IReadOnlyList<WebAppInfo> apps, IReadOnlyList<StorageAccountInfo> accounts, IReadOnlyList<string> errors)
    {
        var node = new AzureNodeViewModel(AzureNodeKind.Subscription, sub.Name) { Subscription = sub, Detail = sub.Id };
        foreach (var error in errors)
            node.Children.Add(new AzureNodeViewModel(AzureNodeKind.Message, error));
        var groups = vaults.Select(v => v.ResourceGroup).Concat(apps.Select(a => a.ResourceGroup)).Concat(accounts.Select(a => a.ResourceGroup))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var rg = new AzureNodeViewModel(AzureNodeKind.ResourceGroup, group) { IsExpanded = true };
            foreach (var vault in vaults.Where(v => string.Equals(v.ResourceGroup, group, StringComparison.OrdinalIgnoreCase)))
                rg.Children.Add(new AzureNodeViewModel(AzureNodeKind.Vault, vault.Name) { Vault = vault, Detail = vault.Location });
            foreach (var app in apps.Where(a => string.Equals(a.ResourceGroup, group, StringComparison.OrdinalIgnoreCase)))
            {
                var stopped = app.State is not null && !string.Equals(app.State, "Running", StringComparison.OrdinalIgnoreCase);
                var detail = $"{WebAppKinds.Describe(app.Kind)} · {app.Location}" + (stopped ? $" · {app.State}" : string.Empty);
                rg.Children.Add(new AzureNodeViewModel(AzureNodeKind.WebApp, app.Name) { WebApp = app, Detail = detail });
            }
            foreach (var account in accounts.Where(a => string.Equals(a.ResourceGroup, group, StringComparison.OrdinalIgnoreCase)))
                rg.Children.Add(new AzureNodeViewModel(AzureNodeKind.StorageAccount, account.Name) { StorageAccount = account, Detail = StorageCatalog.Describe(account) });
            node.Children.Add(rg);
        }

        // Keep subscriptions in name order as they arrive.
        var index = 0;
        while (index < Roots.Count && string.Compare(Roots[index].DisplayName, node.DisplayName, StringComparison.OrdinalIgnoreCase) < 0) index++;
        Roots.Insert(index, node);
        if (!string.IsNullOrWhiteSpace(FilterText)) node.ApplyFilter(FilterText);
    }

    // ---------- open and pin ----------

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void OpenSelected()
    {
        if (SelectedNode?.Vault is { } vault) _shell.OpenVault(vault.VaultUri, vault.TenantId, vault.Name, null);
        if (SelectedNode?.WebApp is { } app && WebAppId.Parse(app.ResourceId) is { } id)
        {
            var tenant = string.Equals(app.TenantId, _auth.HomeTenantId, StringComparison.OrdinalIgnoreCase) ? null : app.TenantId;
            _shell.OpenWebApp(id, tenant, app.Name, null);
        }
        if (SelectedNode?.StorageAccount is { } account && StorageAccountId.Parse(account.ResourceId) is { } accountId)
        {
            var tenant = string.Equals(account.TenantId, _auth.HomeTenantId, StringComparison.OrdinalIgnoreCase) ? null : account.TenantId;
            _shell.OpenStorage(accountId, tenant, account.Name, null, null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private void PinSelected()
    {
        if (SelectedNode?.Vault is { } vault) _shell.PinVault(vault);
        if (SelectedNode?.WebApp is { } app) _shell.PinWebApp(app);
        if (SelectedNode?.StorageAccount is { } account) _shell.PinStorage(account);
    }

    [RelayCommand]
    private void OpenNamed()
    {
        var uri = VaultNames.NormaliseVaultUri(OpenByName);
        if (uri is null)
        {
            ErrorText = "Enter a vault name such as kv-prod, or an address such as https://kv-prod.vault.azure.net.";
            return;
        }
        ErrorText = string.Empty;
        _shell.OpenVault(uri, null, VaultNames.VaultNameOf(uri), null);
        OpenByName = string.Empty;
    }

    [RelayCommand]
    private void DismissError()
    {
        ErrorText = string.Empty;
        ErrorNeedsSignIn = false;
    }

    private async Task<bool> RunAsync(string busy, string action, Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        ErrorText = string.Empty;
        ErrorNeedsSignIn = false;
        BusyText = busy + "...";
        IsBusy = true;
        try
        {
            await work(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            ErrorText = KeyVaultErrors.Describe(ex, action);
            ErrorNeedsSignIn = AzureAuth.IsInteractionRequired(ex) || ex is Azure.RequestFailedException { Status: 401 }
                || ex is Azure.Identity.AuthenticationFailedException || _auth.NeedsSignIn;
            _log.Warn($"{action} failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            return false;
        }
        finally
        {
            // A newer operation may already own the busy state.
            if (!ct.IsCancellationRequested)
            {
                IsBusy = false;
                BusyText = string.Empty;
            }
        }
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _auth.Changed -= OnAuthChanged;
    }
}
