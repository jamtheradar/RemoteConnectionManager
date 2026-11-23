using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using RemoteConnectionManager.App.Services;

namespace RemoteConnectionManager.App.Views;

/// <summary>Picks the folder to move a tree item into. Places it cannot go are shown greyed with the reason.</summary>
public partial class MoveToWindow : Window
{
    private readonly IReadOnlyList<MoveTarget> _all;

    public MoveTarget? Result { get; private set; }

    public MoveToWindow(string itemName, IReadOnlyList<MoveTarget> targets)
    {
        InitializeComponent();
        _all = targets;
        Title = $"Move {itemName}";
        MessageText.Text = $"Move '{itemName}' to:";
        Targets.ItemsSource = _all;
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FilterBox.Text.Trim();
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // While filtering, show matching folders flat so a deep folder is easy to spot.
        Targets.ItemsSource = filter.Length == 0
            ? _all
            : _all.Where(t => t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).Select(t => t with { Depth = 0 }).ToList();
        if (Targets.Items.Cast<MoveTarget>().FirstOrDefault(t => t.Allowed) is { } first && filter.Length > 0) Targets.SelectedItem = first;
    }

    /// <summary>Down arrow from the filter goes to the list, like a search box.</summary>
    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || Targets.Items.Count == 0) return;
        if (Targets.SelectedItem is null) Targets.SelectedItem = Targets.Items.Cast<MoveTarget>().FirstOrDefault(t => t.Allowed);
        (Targets.ItemContainerGenerator.ContainerFromItem(Targets.SelectedItem) as ListBoxItem)?.Focus();
        e.Handled = true;
    }

    private void Targets_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OkButton.IsEnabled = Targets.SelectedItem is MoveTarget { Allowed: true };

    private void Targets_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Targets.SelectedItem is MoveTarget { Allowed: true }) Ok_Click(sender, e);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Targets.SelectedItem is not MoveTarget { Allowed: true } target) return;
        Result = target;
        DialogResult = true;
    }
}

/// <summary>Indents a row by its depth in the folder tree.</summary>
public sealed class DepthToIndentConverter : IValueConverter
{
    public static DepthToIndentConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is int depth ? depth * 16 : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
