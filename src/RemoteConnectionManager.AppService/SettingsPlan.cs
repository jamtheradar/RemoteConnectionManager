namespace RemoteConnectionManager.AppService;

/// <summary>
/// What a save will change, worked out from the settings as loaded and as edited. Names only: the
/// summary is shown in the confirmation and written to the app log, so it never carries a value.
/// </summary>
public sealed class SettingsPlan
{
    private SettingsPlan()
    {
    }

    public IReadOnlyList<string> AddedSettings { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> ChangedSettings { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> RemovedSettings { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> AddedConnectionStrings { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> ChangedConnectionStrings { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> RemovedConnectionStrings { get; private init; } = Array.Empty<string>();

    /// <summary>True when the app settings list has to be sent (Azure replaces the whole list). A slot tick alone does not count.</summary>
    public bool AppSettingsChanged => _appSettingsValuesChanged;
    /// <summary>True when the connection strings list has to be sent. A slot tick alone does not count.</summary>
    public bool ConnectionStringsChanged => _connectionStringValuesChanged;
    /// <summary>True when a "deployment slot setting" tick was added or taken off.</summary>
    public bool SlotFlagsChanged { get; private init; }

    public bool HasChanges => AppSettingsChanged || ConnectionStringsChanged || SlotFlagsChanged;

    /// <summary>True when saving restarts the app (Azure restarts it whenever settings or connection strings are replaced).</summary>
    public bool RestartsApp => AppSettingsChanged || ConnectionStringsChanged;

    private bool _appSettingsValuesChanged;
    private bool _connectionStringValuesChanged;

    public static SettingsPlan Create(WebAppSettings original, WebAppSettings edited)
    {
        var (addedS, changedS, removedS, valuesS, flagsS) = Compare(
            original.AppSettings, edited.AppSettings, s => s.Name, (a, b) => a.Value == b.Value, s => s.SlotSetting);
        var (addedC, changedC, removedC, valuesC, flagsC) = Compare(
            original.ConnectionStrings, edited.ConnectionStrings, c => c.Name,
            (a, b) => a.Value == b.Value && string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase), c => c.SlotSetting);

        return new SettingsPlan
        {
            AddedSettings = addedS,
            ChangedSettings = changedS,
            RemovedSettings = removedS,
            AddedConnectionStrings = addedC,
            ChangedConnectionStrings = changedC,
            RemovedConnectionStrings = removedC,
            _appSettingsValuesChanged = valuesS,
            _connectionStringValuesChanged = valuesC,
            SlotFlagsChanged = flagsS || flagsC,
        };
    }

    /// <summary>
    /// Added, changed (value, or the slot tick) and removed names. "Values changed" is true when the
    /// list itself has to be sent; a slot tick alone only changes the site's slot setting names.
    /// </summary>
    private static (List<string> Added, List<string> Changed, List<string> Removed, bool ValuesChanged, bool FlagsChanged) Compare<T>(
        IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, string> name, Func<T, T, bool> sameValue, Func<T, bool> slot)
    {
        var old = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in before) old[name(item)] = item;
        var now = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in after) now[name(item)] = item;

        var added = new List<string>();
        var changed = new List<string>();
        var removed = new List<string>();
        var values = false;
        var flags = false;

        foreach (var item in after)
        {
            var n = name(item);
            if (!old.TryGetValue(n, out var was))
            {
                added.Add(n);
                values = true;
                if (slot(item)) flags = true;
                continue;
            }
            var valueDiffers = !sameValue(was, item) || !string.Equals(name(was), n, StringComparison.Ordinal);
            var flagDiffers = slot(was) != slot(item);
            if (valueDiffers) values = true;
            if (flagDiffers) flags = true;
            if (valueDiffers || flagDiffers) changed.Add(n);
        }
        foreach (var item in before)
        {
            if (now.ContainsKey(name(item))) continue;
            removed.Add(name(item));
            values = true;
        }
        return (added, changed, removed, values, flags);
    }

    /// <summary>One line per kind of change, for the confirmation and the app log. Names only.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        Add(lines, "Add setting", AddedSettings);
        Add(lines, "Change setting", ChangedSettings);
        Add(lines, "Remove setting", RemovedSettings);
        Add(lines, "Add connection string", AddedConnectionStrings);
        Add(lines, "Change connection string", ChangedConnectionStrings);
        Add(lines, "Remove connection string", RemovedConnectionStrings);
        return lines;
    }

    private static void Add(List<string> lines, string label, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return;
        var shown = names.Take(8).ToList();
        var more = names.Count > shown.Count ? $" and {names.Count - shown.Count} more" : string.Empty;
        lines.Add($"{label}{(names.Count > 1 ? "s" : string.Empty)}: {string.Join(", ", shown)}{more}");
    }

    /// <summary>
    /// The site's new slot setting names. Names that belong to other slots (not in this list before or
    /// after) are kept as they were; a name removed here keeps whatever it had, because another slot
    /// may still use it.
    /// </summary>
    public static IReadOnlyList<string> MergeSlotNames<T>(IEnumerable<string>? current, IReadOnlyList<T> before, IReadOnlyList<T> after,
        Func<T, string> name, Func<T, bool> slot)
    {
        var result = new List<string>(current ?? Array.Empty<string>());
        var old = before.ToDictionary(name, slot, StringComparer.OrdinalIgnoreCase);
        foreach (var item in after)
        {
            var n = name(item);
            var wanted = slot(item);
            var had = old.TryGetValue(n, out var was) && was;
            if (wanted == had && old.ContainsKey(n)) continue;
            result.RemoveAll(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
            if (wanted) result.Add(n);
        }
        return result;
    }

    /// <summary>True when two loads hold the same names, values, types and slot ticks (order does not matter).</summary>
    public static bool Same(WebAppSettings a, WebAppSettings b)
    {
        var plan = Create(a, b);
        return !plan.HasChanges;
    }

    /// <summary>A problem with the edited list that Azure would refuse or that would lose data, or null.</summary>
    public static string? Validate(WebAppSettings edited)
    {
        var problem = CheckNames(edited.AppSettings.Select(s => s.Name), "setting");
        if (problem is not null) return problem;
        problem = CheckNames(edited.ConnectionStrings.Select(c => c.Name), "connection string");
        if (problem is not null) return problem;
        foreach (var c in edited.ConnectionStrings)
        {
            if (!WebAppSettings.ConnectionStringTypes.Contains(c.Type, StringComparer.OrdinalIgnoreCase))
                return $"Connection string '{c.Name}' has an unknown type '{c.Type}'.";
        }
        return null;
    }

    private static string? CheckNames(IEnumerable<string> names, string noun)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names)
        {
            var n = raw ?? string.Empty;
            if (n.Trim().Length == 0) return $"Every {noun} needs a name.";
            if (n != n.Trim()) return $"The {noun} name '{n}' starts or ends with a space.";
            if (n.Contains('=')) return $"The {noun} name '{n}' cannot contain '='.";
            if (!seen.Add(n)) return $"There are two {noun}s called '{n}'. Names are not case sensitive in Azure.";
        }
        return null;
    }
}
