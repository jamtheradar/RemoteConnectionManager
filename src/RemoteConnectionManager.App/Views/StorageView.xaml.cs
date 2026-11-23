using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

/// <summary>
/// The blob browser. Code here only bridges what bindings cannot: the list's multi selection, double
/// click, keyboard shortcuts, and files dropped from Explorer.
/// </summary>
public partial class StorageView : UserControl
{
    public StorageView()
    {
        InitializeComponent();
    }

    private StorageTabViewModel? Vm => DataContext as StorageTabViewModel;

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm?.SetSelection(EntryList.SelectedItems.OfType<BlobEntryRow>());

    private void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double click on a row, not on the scroll bar or empty space.
        if (e.OriginalSource is DependencyObject source && FindAncestor<ListBoxItem>(source)?.DataContext is BlobEntryRow row)
        {
            Vm?.OpenCommand.Execute(row);
            e.Handled = true;
        }
    }

    private void EntryList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Enter when EntryList.SelectedItems.Count == 1 && EntryList.SelectedItem is BlobEntryRow row:
                vm.OpenCommand.Execute(row);
                break;
            case Key.Back:
                if (vm.UpCommand.CanExecute(null)) vm.UpCommand.Execute(null);
                break;
            case Key.Delete:
                if (vm.DeleteCommand.CanExecute(null)) vm.DeleteCommand.Execute(null);
                break;
            case Key.F2:
                if (vm.RenameCommand.CanExecute(null)) vm.RenameCommand.Execute(null);
                break;
            case Key.F5:
                vm.RefreshCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void DropArea_DragOver(object sender, DragEventArgs e)
    {
        var ok = Vm?.HasContainer == true && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void DropArea_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void DropArea_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (Vm is not { HasContainer: true } vm || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        // Let Explorer's drag finish before any dialog (overwrite question) appears.
        Dispatcher.BeginInvoke(() => _ = vm.UploadPathsAsync(paths));
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }
}
