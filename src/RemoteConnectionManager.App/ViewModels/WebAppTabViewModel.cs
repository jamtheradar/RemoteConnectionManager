using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.AppService;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.KeyVault;

namespace RemoteConnectionManager.App.ViewModels;

public enum WebAppSectionKind
{
    Overview,
    Settings,
    ConnectionStrings,
}

public sealed record WebAppSection(WebAppSectionKind Kind, string Title, string Glyph, string IconBrushKey);

/// <summary>A deployment slot in the slot picker. A null name is the production site.</summary>
public sealed record WebAppSlotChoice(string? Name)
{
    public string Title => Name ?? "Production";
}

/// <summary>
/// One app setting or connection string being edited. Holds the value in memory while the tab is open
/// (Azure returns all values in one call); the list shows it masked until the person shows values.
/// </summary>
public sealed partial class SettingRowViewModel : ObservableObject
{
    public const string Mask = "••••••••";

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _value;
    [ObservableProperty] private string _type;
    [ObservableProperty] private bool _slotSetting;
    [ObservableProperty] private bool _revealed;

    public SettingRowViewModel(bool isConnectionString, string name, string value, string type, bool slotSetting, bool isNew)
    {
        IsConnectionString = isConnectionString;
        _name = name;
        _value = value;
        _type = type;
        _slotSetting = slotSetting;
        IsNew = isNew;
        _revealed = isNew;
        if (!isNew) Original = (name, value, type, slotSetting);
    }

    public bool IsConnectionString { get; }
    public bool IsNew { get; }
    private (string Name, string Value, string Type, bool Slot)? Original { get; }

    public bool IsModified => !IsNew && Original is { } o &&
        (o.Name != Name || o.Value != Value || !string.Equals(o.Type, Type, StringComparison.OrdinalIgnoreCase) || o.Slot != SlotSetting);
    public string ChangeText => IsNew ? "new" : IsModified ? "changed" : string.Empty;
    public bool IsKeyVaultReference => KeyVaultReference.LooksLikeReference(Value);
    public KeyVaultReference? VaultReference => KeyVaultReference.Parse(Value);
    /// <summary>Key Vault references are addresses, not secrets, so they always show.</summary>
    public bool ShowsValue => Revealed || IsKeyVaultReference;
    public string DisplayValue => ShowsValue ? Value : (Value.Length == 0 ? "(empty)" : Mask);
    public string SlotText => SlotSetting ? "Slot setting" : string.Empty;
    public string SourceText => IsKeyVaultReference ? "Key Vault" : "App Service";

    partial void OnNameChanged(string value) => Changed();
    partial void OnValueChanged(string value)
    {
        Changed();
        OnPropertyChanged(nameof(IsKeyVaultReference));
        OnPropertyChanged(nameof(VaultReference));
        OnPropertyChanged(nameof(SourceText));
    }
    partial void OnTypeChanged(string value) => Changed();
    partial void OnSlotSettingChanged(bool value)
    {
        Changed();
        OnPropertyChanged(nameof(SlotText));
    }
    partial void OnRevealedChanged(bool value) => OnPropertyChanged(nameof(ShowsValue));

    private void Changed()
    {
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(ChangeText));
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(ShowsValue));
    }

    public AppSettingValue ToSetting() => new(Name, Value, SlotSetting);
    public ConnectionStringValue ToConnectionString() => new(Name, Value, Type, SlotSetting);

    /// <summary>Drops the value from the row so it does not linger after the tab closes.</summary>
    public void Forget()
    {
        _value = string.Empty;
    }
}

/// <summary>
/// An open Azure Web App or Function App: the portal's Overview essentials, the app settings and
/// connection strings (Environment variables), start, stop and restart, and deployment slots. Azure IAM
/// decides what is allowed. Edits are kept here until Save, which re-reads the settings first and refuses
/// if someone else changed them. Values are never logged.
/// </summary>
public sealed partial class WebAppTabViewModel : TabViewModelBase, IDisposable
{
    private static readonly TimeSpan ValuesVisibleFor = TimeSpan.FromSeconds(120);

    private readonly ShellViewModel _shell;
    private readonly WebAppConnection? _entry;
    private readonly string? _tenantId;
    private readonly AzureAuth _auth;
    private readonly ClipboardGuard _clipboard;
    private readonly IDialogService _dialogs;
    private readonly AppLog _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _hideTimer;
    private CancellationTokenSource? _runCts;
    private WebAppClient? _client;
    private object? _clientCredential;
    /// <summary>The settings as last loaded from Azure, for the change list and the save check.</summary>
    private WebAppSettings? _loaded;
    private bool _slotsLoaded;
    private bool _changingSlot;

    [ObservableProperty] private WebAppSection _selectedSection;
    [ObservableProperty] private WebAppSlotChoice _selectedSlot;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = string.Empty;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _errorNeedsSignIn;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private WebAppOverview? _overview;
    [ObservableProperty] private IReadOnlyList<PropertyRow> _essentialsLeft = Array.Empty<PropertyRow>();
    [ObservableProperty] private IReadOnlyList<PropertyRow> _essentialsRight = Array.Empty<PropertyRow>();
    [ObservableProperty] private IReadOnlyList<PropertyRow> _configurationRows = Array.Empty<PropertyRow>();
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _valuesShown;
    [ObservableProperty] private SettingRowViewModel? _selectedRow;
    [ObservableProperty] private bool _hasPendingChanges;
    [ObservableProperty] private string _pendingText = string.Empty;
    [ObservableProperty] private string _settingsError = string.Empty;

    public WebAppId BaseId { get; }
    public WebAppId CurrentId => BaseId.ForSlot(SelectedSlot.Name);
    public string ResourceIdText => CurrentId.ToString();

    public IReadOnlyList<WebAppSection> Sections { get; } =
    [
        new(WebAppSectionKind.Overview, "Overview", "", "Icon.WebApp"),
        new(WebAppSectionKind.Settings, "Environment variables", "", "Icon.Secret"),
        new(WebAppSectionKind.ConnectionStrings, "Connection strings", "", "Icon.Key"),
    ];

    public ObservableCollection<WebAppSlotChoice> Slots { get; } = new();
    /// <summary>All rows of the current list (settings or connection strings) including those the filter hides.</summary>
    private readonly List<SettingRowViewModel> _settingRows = new();
    private readonly List<SettingRowViewModel> _connectionRows = new();
    public ObservableCollection<SettingRowViewModel> Rows { get; } = new();

    public IReadOnlyList<string> ConnectionStringTypes => WebAppSettings.ConnectionStringTypes;

    public bool IsOverview => SelectedSection.Kind == WebAppSectionKind.Overview;
    public bool IsSettingsList => SelectedSection.Kind != WebAppSectionKind.Overview;
    public bool IsConnectionStrings => SelectedSection.Kind == WebAppSectionKind.ConnectionStrings;
    public bool HasSlots => Slots.Count > 1;
    public bool HasSelection => SelectedRow is not null;
    public bool HasError => ErrorText.Length > 0;
    public bool HasSettingsError => SettingsError.Length > 0;
    public bool SettingsLoaded => _loaded is not null;
    public bool ShowBusyOverlay => IsBusy && (IsOverview ? Overview is null : _loaded is null);
    public bool NeedsSignIn => _auth.NeedsSignIn || !_auth.IsSignedIn;
    public string AccountText => _auth.AccountName is not { } name ? "Not signed in to Azure"
        : _auth.IsPrompting ? $"{name}: waiting for sign in"
        : _auth.NeedsSignIn ? $"{name}: sign in expired"
        : name;

    public string StateText => Overview is null ? string.Empty
        : !Overview.Enabled ? "Disabled"
        : Overview.State ?? "Unknown";
    public bool IsRunning => Overview?.IsRunning == true;
    public bool IsStopped => Overview?.IsStopped == true;
    public bool CanBrowse => !string.IsNullOrEmpty(Overview?.DefaultHostName);
    public string KindText => Overview is null ? string.Empty : WebAppKinds.Describe(Overview.Kind);
    public string AddLabel => IsConnectionStrings ? "Add connection string" : "Add setting";
    public string EmptyText => IsBusy || Rows.Count > 0 || _loaded is null ? string.Empty
        : FilterText.Length > 0 ? "Nothing matches the filter."
        : IsConnectionStrings ? "This app has no connection strings." : "This app has no app settings.";

    public override Guid? EntryId => _entry?.Id;
    public override bool IsLive => true;

    public WebAppTabViewModel(WebAppId id, string? tenantId, string title, WebAppConnection? entry, ShellViewModel shell,
        AzureAuth auth, ClipboardGuard clipboard, IDialogService dialogs, AppLog log)
    {
        BaseId = id.Site;
        _tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
        _entry = entry;
        _shell = shell;
        _auth = auth;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _log = log;
        Title = $"(APP) {title}";
        Glyph = "";
        IconBrushKey = "Icon.WebApp";

        _hideTimer = new DispatcherTimer { Interval = ValuesVisibleFor };
        _hideTimer.Tick += (_, _) => ValuesShown = false;

        Slots.Add(new WebAppSlotChoice(null));
        if (id.SlotName is { } slot) Slots.Add(new WebAppSlotChoice(slot));
        _selectedSlot = Slots[^1];
        _selectedSection = Sections[0];
        Slots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSlots));
        _auth.Changed += OnAuthChanged;
        _ = LoadOverviewAsync();
    }

    private void OnAuthChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnAuthChanged(sender, e));
            return;
        }
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(NeedsSignIn));
        if (IsBusy && _auth.IsPrompting)
            BusyText = "Waiting for you to sign in to Azure. Look for the Microsoft sign in window, it may be behind this one...";
    }

    // ---------- property plumbing ----------

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSettingsErrorChanged(string value) => OnPropertyChanged(nameof(HasSettingsError));
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBusyOverlay));
        OnPropertyChanged(nameof(EmptyText));
    }

    partial void OnOverviewChanged(WebAppOverview? value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(CanBrowse));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(ShowBusyOverlay));
        BuildOverviewRows(value);
    }

    partial void OnSelectedSectionChanged(WebAppSection value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsSettingsList));
        OnPropertyChanged(nameof(IsConnectionStrings));
        OnPropertyChanged(nameof(AddLabel));
        OnPropertyChanged(nameof(ShowBusyOverlay));
        SelectedRow = null;
        ApplyFilter();
        if (value.Kind != WebAppSectionKind.Overview && _loaded is null && !IsBusy) _ = LoadSettingsAsync();
    }

    partial void OnSelectedSlotChanged(WebAppSlotChoice? oldValue, WebAppSlotChoice newValue)
    {
        if (_changingSlot || oldValue is null || oldValue == newValue) return;
        if (HasPendingChanges && !_dialogs.Confirm(
                $"You have unsaved changes to the settings of {BaseId.ForSlot(oldValue.Name).DisplayName}. Switch to {newValue.Title} and throw them away?",
                "Unsaved changes"))
        {
            _changingSlot = true;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                SelectedSlot = oldValue;
                _changingSlot = false;
            });
            return;
        }
        OnPropertyChanged(nameof(CurrentId));
        OnPropertyChanged(nameof(ResourceIdText));
        Title = $"(APP) {CurrentId.DisplayName}";
        ClearSettings();
        Overview = null;
        _ = ReloadForSlotAsync();
    }

    private async Task ReloadForSlotAsync()
    {
        _runCts?.Cancel();
        await LoadOverviewAsync();
        if (IsSettingsList) await LoadSettingsAsync();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnValuesShownChanged(bool value)
    {
        foreach (var row in _settingRows.Concat(_connectionRows)) row.Revealed = value || row.IsNew;
        _hideTimer.Stop();
        if (value) _hideTimer.Start();
    }

    partial void OnSelectedRowChanged(SettingRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        RemoveCommand.NotifyCanExecuteChanged();
        CopyValueCommand.NotifyCanExecuteChanged();
        OpenReferencedVaultCommand.NotifyCanExecuteChanged();
    }

    // ---------- overview ----------

    [RelayCommand]
    private async Task Refresh()
    {
        if (IsBusy) return;
        if (HasPendingChanges && !_dialogs.Confirm("Reload the settings from Azure and throw away your unsaved changes?", "Unsaved changes"))
            return;
        await LoadOverviewAsync();
        if (IsSettingsList || _loaded is not null) await LoadSettingsAsync();
        if (!HasError) StatusMessage = $"Refreshed at {DateTime.Now:HH:mm:ss}.";
    }

    private async Task LoadOverviewAsync()
    {
        var id = CurrentId;
        WebAppOverview? overview = null;
        IReadOnlyList<string>? slots = null;
        await RunAsync("Loading the app", "Loading the app", async (client, ct) =>
        {
            overview = await client.GetOverviewAsync(id, ct);
            if (!_slotsLoaded)
            {
                try { slots = await client.ListSlotNamesAsync(ct); }
                catch (Azure.RequestFailedException ex) { _log.Warn($"[{BaseId.DisplayName}] listing slots failed: {ex.Status}"); }
            }
        });
        if (id != CurrentId) return;
        if (overview is not null) Overview = overview;
        if (slots is not null)
        {
            _slotsLoaded = true;
            foreach (var name in slots)
                if (!Slots.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) Slots.Add(new WebAppSlotChoice(name));
        }
    }

    private void BuildOverviewRows(WebAppOverview? o)
    {
        if (o is null)
        {
            EssentialsLeft = EssentialsRight = ConfigurationRows = Array.Empty<PropertyRow>();
            return;
        }
        static string YesNo(bool? value) => value is null ? "unknown" : value.Value ? "On" : "Off";

        EssentialsLeft =
        [
            new("Resource group", o.ResourceGroup),
            new("Status", StateText + (o.AvailabilityState is { } a && !a.Equals("Normal", StringComparison.OrdinalIgnoreCase) ? $" ({a})" : string.Empty)),
            new("Location", o.Location),
            new("Subscription ID", o.SubscriptionId),
            new("Kind", WebAppKinds.Describe(o.Kind)),
            new("Tags", o.Tags.Count == 0 ? "none" : string.Join(", ", o.Tags.Select(t => $"{t.Key}={t.Value}"))),
        ];

        var right = new List<PropertyRow>
        {
            new("Default domain", o.DefaultHostName ?? "none"),
            new("App Service plan", o.AppServicePlanName is null ? "unknown" : o.AppServicePlanName + (o.PricingTier is { } t ? $" ({t})" : string.Empty)),
            new("Operating system", o.IsLinux ? "Linux" : "Windows"),
            new("Runtime stack", o.RuntimeStack ?? "unknown"),
            new("Instances", o.InstanceCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"),
            new("Health check", string.IsNullOrWhiteSpace(o.HealthCheckPath) ? "Not configured" : o.HealthCheckPath),
        };
        if (o.CustomDomains.Count > 0) right.Add(new("Custom domains", string.Join(", ", o.CustomDomains)));
        EssentialsRight = right;

        var config = new List<PropertyRow>
        {
            new("HTTPS only", YesNo(o.HttpsOnly)),
            new("Minimum TLS", o.MinTlsVersion ?? "unknown"),
            new("FTP state", o.FtpsState ?? "unknown"),
            new("Always on", YesNo(o.AlwaysOn)),
            new("HTTP 2.0", YesNo(o.Http20)),
            new("Web sockets", YesNo(o.WebSockets)),
            new("Platform", o.Use32BitWorker is null ? "unknown" : o.Use32BitWorker.Value ? "32 bit" : "64 bit"),
            new("Client certificates", YesNo(o.ClientCertificates)),
            new("Public network access", string.IsNullOrWhiteSpace(o.PublicNetworkAccess) ? "Enabled (default)" : o.PublicNetworkAccess),
            new("VNet integration", o.VirtualNetworkSubnet is { } subnet ? ShortName(subnet) : "none"),
            new("Managed identity", o.ManagedIdentity ?? "None"),
            new("Outbound IPs", o.OutboundIpAddresses.Count == 0 ? "unknown" : string.Join(", ", o.OutboundIpAddresses)),
            new("Last modified", o.LastModified is { } m ? m.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "unknown"),
            new("Resource ID", o.ResourceId),
        };
        foreach (var warning in o.Warnings) config.Add(new("Note", warning));
        ConfigurationRows = config;
    }

    private static string ShortName(string resourceId)
    {
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // .../virtualNetworks/{vnet}/subnets/{subnet}
        return parts.Length >= 4 ? $"{parts[^3]}/{parts[^1]}" : resourceId;
    }

    // ---------- start, stop, restart, browse ----------

    [RelayCommand]
    private async Task Start()
    {
        var id = CurrentId;
        if (await WriteAsync($"Starting {id.DisplayName}", "Starting the app", (c, ct) => c.StartAsync(id, ct)))
        {
            StatusMessage = $"Started {id.DisplayName}.";
            await LoadOverviewAsync();
        }
    }

    [RelayCommand]
    private async Task Stop()
    {
        var id = CurrentId;
        if (!_dialogs.Confirm($"Stop {id.DisplayName}?\n\nThe site stops serving requests until it is started again.", "Stop app")) return;
        if (await WriteAsync($"Stopping {id.DisplayName}", "Stopping the app", (c, ct) => c.StopAsync(id, ct)))
        {
            StatusMessage = $"Stopped {id.DisplayName}.";
            await LoadOverviewAsync();
        }
    }

    [RelayCommand]
    private async Task Restart()
    {
        var id = CurrentId;
        if (!_dialogs.Confirm($"Restart {id.DisplayName}?\n\nRequests may fail or be slow for a short time while it starts again.", "Restart app")) return;
        if (await WriteAsync($"Restarting {id.DisplayName}", "Restarting the app", (c, ct) => c.RestartAsync(id, ct)))
        {
            StatusMessage = $"Restart sent to {id.DisplayName}. It can take a minute to come back.";
            await LoadOverviewAsync();
        }
    }

    [RelayCommand]
    private void Browse()
    {
        if (Overview?.DefaultHostName is { Length: > 0 } host && Uri.TryCreate($"https://{host}/", UriKind.Absolute, out var uri))
        {
            WebSessionTabViewModel.OpenExternally(uri, _log);
            StatusMessage = $"Opened {host} in the default browser.";
        }
    }

    [RelayCommand]
    private void OpenInPortal()
    {
        if (Uri.TryCreate(CurrentId.PortalUrl(_tenantId ?? _auth.HomeTenantId), UriKind.Absolute, out var uri))
        {
            WebSessionTabViewModel.OpenExternally(uri, _log);
            StatusMessage = "Opened the app in the Azure portal.";
        }
    }

    [RelayCommand]
    private void CopyResourceId()
    {
        if (_clipboard.CopyPlain(ResourceIdText)) StatusMessage = "Copied the resource ID.";
    }

    [RelayCommand]
    private void CopyRow(PropertyRow? row)
    {
        if (row is not null && _clipboard.CopyPlain(row.Value)) StatusMessage = $"Copied {row.Label.ToLowerInvariant()}.";
    }

    // ---------- settings: load and list ----------

    private async Task LoadSettingsAsync()
    {
        var id = CurrentId;
        WebAppSettings? settings = null;
        SettingsError = string.Empty;
        await RunAsync("Reading the app settings", "Reading the app settings", async (client, ct) => settings = await client.GetSettingsAsync(id, ct),
            showInSettings: true);
        if (id != CurrentId || settings is null) return;
        ClearSettings();
        _loaded = settings;
        foreach (var s in settings.AppSettings)
            _settingRows.Add(Track(new SettingRowViewModel(false, s.Name, s.Value, string.Empty, s.SlotSetting, isNew: false) { Revealed = ValuesShown }));
        foreach (var c in settings.ConnectionStrings)
            _connectionRows.Add(Track(new SettingRowViewModel(true, c.Name, c.Value, c.Type, c.SlotSetting, isNew: false) { Revealed = ValuesShown }));
        OnPropertyChanged(nameof(SettingsLoaded));
        OnPropertyChanged(nameof(ShowBusyOverlay));
        ApplyFilter();
        UpdatePending();
        _log.Info($"[{id.DisplayName}] read {settings.AppSettings.Count} app settings and {settings.ConnectionStrings.Count} connection strings");
    }

    private SettingRowViewModel Track(SettingRowViewModel row)
    {
        row.PropertyChanged += OnRowChanged;
        return row;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingRowViewModel.Name) or nameof(SettingRowViewModel.Value)
            or nameof(SettingRowViewModel.Type) or nameof(SettingRowViewModel.SlotSetting))
            UpdatePending();
        if (e.PropertyName == nameof(SettingRowViewModel.Value) && ReferenceEquals(sender, SelectedRow))
            OpenReferencedVaultCommand.NotifyCanExecuteChanged();
    }

    private void ClearSettings()
    {
        foreach (var row in _settingRows.Concat(_connectionRows))
        {
            row.PropertyChanged -= OnRowChanged;
            row.Forget();
        }
        _settingRows.Clear();
        _connectionRows.Clear();
        Rows.Clear();
        SelectedRow = null;
        _loaded = null;
        SettingsError = string.Empty;
        OnPropertyChanged(nameof(SettingsLoaded));
        UpdatePending();
    }

    private List<SettingRowViewModel> CurrentList => IsConnectionStrings ? _connectionRows : _settingRows;

    private void ApplyFilter()
    {
        var selected = SelectedRow;
        Rows.Clear();
        if (IsSettingsList)
        {
            foreach (var row in CurrentList)
            {
                if (FilterText.Length > 0 && !row.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                    && !(row.ShowsValue && row.Value.Contains(FilterText, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Rows.Add(row);
            }
        }
        if (selected is not null && Rows.Contains(selected)) SelectedRow = selected;
        OnPropertyChanged(nameof(EmptyText));
    }

    private WebAppSettings Edited() => new(
        _settingRows.Select(r => r.ToSetting()).ToList(),
        _connectionRows.Select(r => r.ToConnectionString()).ToList());

    private void UpdatePending()
    {
        if (_loaded is null)
        {
            HasPendingChanges = false;
            PendingText = string.Empty;
        }
        else
        {
            var plan = SettingsPlan.Create(_loaded, Edited());
            HasPendingChanges = plan.HasChanges;
            var count = plan.AddedSettings.Count + plan.ChangedSettings.Count + plan.RemovedSettings.Count
                + plan.AddedConnectionStrings.Count + plan.ChangedConnectionStrings.Count + plan.RemovedConnectionStrings.Count;
            PendingText = plan.HasChanges ? $"{count} unsaved change{(count == 1 ? string.Empty : "s")}" : string.Empty;
        }
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    // ---------- settings: edit ----------

    [RelayCommand]
    private void ToggleValues() => ValuesShown = !ValuesShown;

    [RelayCommand]
    private void RevealSelected()
    {
        if (SelectedRow is { } row)
        {
            row.Revealed = true;
            _log.Info($"[{CurrentId.DisplayName}] showed {(row.IsConnectionString ? "connection string" : "setting")} '{row.Name}'");
        }
    }

    [RelayCommand]
    private void Add()
    {
        if (!SettingsLoaded) return;
        var list = CurrentList;
        var baseName = IsConnectionStrings ? "NewConnectionString" : "NEW_SETTING";
        var name = baseName;
        for (var i = 2; list.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{baseName}{i}";
        var row = Track(new SettingRowViewModel(IsConnectionStrings, name, string.Empty, IsConnectionStrings ? "SQLAzure" : string.Empty, false, isNew: true));
        list.Add(row);
        FilterText = string.Empty;
        ApplyFilter();
        SelectedRow = row;
        UpdatePending();
    }

    private bool HasRow() => SelectedRow is not null;

    [RelayCommand(CanExecute = nameof(HasRow))]
    private void Remove()
    {
        if (SelectedRow is not { } row) return;
        var list = row.IsConnectionString ? _connectionRows : _settingRows;
        var index = Rows.IndexOf(row);
        row.PropertyChanged -= OnRowChanged;
        list.Remove(row);
        ApplyFilter();
        SelectedRow = Rows.Count == 0 ? null : Rows[Math.Clamp(index, 0, Rows.Count - 1)];
        UpdatePending();
    }

    [RelayCommand(CanExecute = nameof(HasRow))]
    private void CopyValue()
    {
        if (SelectedRow is not { } row) return;
        StatusMessage = _clipboard.CopySecret(row.Value)
            ? $"Copied the value of '{row.Name}'. The clipboard clears in {ClipboardGuard.ClearAfter.TotalSeconds:0} seconds."
            : "The clipboard is busy, try again.";
        _log.Info($"[{CurrentId.DisplayName}] copied the value of '{row.Name}'");
    }

    private bool CanOpenReferencedVault() => SelectedRow?.VaultReference is not null;

    [RelayCommand(CanExecute = nameof(CanOpenReferencedVault))]
    private void OpenReferencedVault()
    {
        if (SelectedRow?.VaultReference is not { } reference) return;
        _shell.OpenVault(reference.VaultUri, _tenantId, VaultNames.VaultNameOf(reference.VaultUri), null);
    }

    [RelayCommand(CanExecute = nameof(HasPendingChanges))]
    private void Discard()
    {
        if (_loaded is not { } loaded) return;
        if (!_dialogs.Confirm("Throw away your unsaved changes to the settings?", "Discard changes")) return;
        var section = SelectedSection;
        ClearSettings();
        // Rebuild from what was loaded, no need to ask Azure again.
        _loaded = loaded;
        foreach (var s in loaded.AppSettings)
            _settingRows.Add(Track(new SettingRowViewModel(false, s.Name, s.Value, string.Empty, s.SlotSetting, isNew: false) { Revealed = ValuesShown }));
        foreach (var c in loaded.ConnectionStrings)
            _connectionRows.Add(Track(new SettingRowViewModel(true, c.Name, c.Value, c.Type, c.SlotSetting, isNew: false) { Revealed = ValuesShown }));
        OnPropertyChanged(nameof(SettingsLoaded));
        SelectedSection = section;
        ApplyFilter();
        UpdatePending();
        StatusMessage = "Discarded your changes.";
    }

    [RelayCommand(CanExecute = nameof(HasPendingChanges))]
    private async Task Save()
    {
        if (_loaded is not { } loaded) return;
        var edited = Edited();
        var plan = SettingsPlan.Create(loaded, edited);
        if (!plan.HasChanges) return;
        if (SettingsPlan.Validate(edited) is { } problem)
        {
            ErrorText = problem;
            return;
        }

        var id = CurrentId;
        var lines = string.Join("\n", plan.Describe().Select(l => "  • " + l));
        var slotNote = plan.SlotFlagsChanged ? "\n\nSlot setting ticks are kept on the production site and apply to every slot." : string.Empty;
        var restart = plan.RestartsApp ? $"\n\nSaving restarts {id.DisplayName}. Requests may fail for a short time." : string.Empty;
        if (!_dialogs.Confirm($"Save these changes to {id.DisplayName}?\n\n{lines}{slotNote}{restart}", "Save settings")) return;

        var ok = await WriteAsync($"Saving settings to {id.DisplayName}", "Saving the settings", (c, ct) => c.SaveSettingsAsync(id, loaded, edited, ct));
        if (!ok) return;
        _log.Info($"[{id.DisplayName}] saved settings: {string.Join("; ", plan.Describe())}");
        var section = SelectedSection;
        await LoadSettingsAsync();
        SelectedSection = section;
        if (!HasError) StatusMessage = plan.RestartsApp ? $"Saved. {id.DisplayName} is restarting with the new settings." : "Saved.";
    }

    // ---------- sign in and plumbing ----------

    [RelayCommand]
    private async Task SignIn()
    {
        if (IsBusy) return;
        await RunAsync("Signing in to Azure", "Signing in", (_, _) => Task.CompletedTask);
        if (!HasError) await Refresh();
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    [RelayCommand]
    private void DismissError()
    {
        ErrorText = string.Empty;
        ErrorNeedsSignIn = false;
    }

    private async Task<bool> WriteAsync(string busy, string action, Func<WebAppClient, CancellationToken, Task> work)
    {
        var ok = false;
        await RunAsync(busy, action, async (c, ct) =>
        {
            await work(c, ct);
            ok = true;
        });
        if (ok) _log.Info($"[{CurrentId.DisplayName}] {busy}: done");
        return ok;
    }

    /// <summary>Signs in if needed, runs the work, and turns any failure into the error bar.</summary>
    private async Task RunAsync(string busy, string action, Func<WebAppClient, CancellationToken, Task> work, bool showInSettings = false)
    {
        ErrorText = string.Empty;
        ErrorNeedsSignIn = false;
        StatusMessage = string.Empty;
        BusyText = "Checking Azure sign in...";
        IsBusy = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _runCts = cts;
        try
        {
            var ct = cts.Token;
            await _auth.EnsureSignedInAsync(_tenantId, AzureAuth.ArmScope, ct);
            BusyText = busy + "...";
            try
            {
                await work(Client(), ct);
            }
            catch (Exception ex) when (AzureAuth.IsInteractionRequired(ex) || ex is Azure.RequestFailedException { Status: 401 })
            {
                // The token ran out between the check and the call. Sign in again and retry once.
                _log.Info($"[{BaseId.DisplayName}] token refused during '{action}', signing in again");
                BusyText = "Your Azure sign in expired, signing in again...";
                await _auth.EnsureSignedInAsync(_tenantId, AzureAuth.ArmScope, ct);
                BusyText = busy + "...";
                await work(Client(), ct);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            ErrorText = $"{action} was cancelled.";
            ErrorNeedsSignIn = _auth.NeedsSignIn || !_auth.IsSignedIn;
        }
        catch (Exception ex)
        {
            var message = AppServiceErrors.Describe(ex, action);
            if (showInSettings) SettingsError = message;
            else ErrorText = message;
            ErrorNeedsSignIn = AzureAuth.IsInteractionRequired(ex) || ex is Azure.RequestFailedException { Status: 401 }
                || ex is Azure.Identity.AuthenticationFailedException || _auth.NeedsSignIn;
            if (showInSettings && ErrorNeedsSignIn) ErrorText = message;
            _log.Warn($"[{BaseId.DisplayName}] {action} failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
        finally
        {
            if (ReferenceEquals(_runCts, cts)) _runCts = null;
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>Rebuilt when sign in hands out a new credential.</summary>
    private WebAppClient Client()
    {
        var credential = _auth.Credential(_tenantId);
        if (_client is null || !ReferenceEquals(_clientCredential, credential))
        {
            _client = new WebAppClient(BaseId, credential);
            _clientCredential = credential;
        }
        return _client;
    }

    /// <summary>Asks before closing a tab with unsaved setting changes.</summary>
    public bool ConfirmClose() =>
        !HasPendingChanges || _dialogs.Confirm($"You have unsaved changes to the settings of {CurrentId.DisplayName}. Close the tab and throw them away?", "Unsaved changes");

    public void Dispose()
    {
        _auth.Changed -= OnAuthChanged;
        _hideTimer.Stop();
        ClearSettings();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
