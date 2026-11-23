using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

public partial class ShellWindow : Window
{
    private readonly ShellViewModel _vm;

    public ShellWindow(ShellViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        _expandTimer.Tick += ExpandTimer_Tick;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }
        // Bring detached sessions home while this window can still host them.
        _vm.DockAllSessions();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PersistSettings();
        base.OnClosed(e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.G && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            GeneratePassword_Click(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    // ----- tree -----

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue as TreeNodeViewModel;

    // A WPF TreeView does not select on right click, so the context menu would act on whatever was
    // selected before (or nothing). Select the item under the mouse first, like Explorer does.
    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemAt(source) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedNode?.Entry is { } entry && e.OriginalSource is DependencyObject source && IsInsideHeader(source))
        {
            _vm.OpenEntry(entry);
            e.Handled = true;
        }
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when _vm.SelectedNode?.Entry is { } entry:
                _vm.OpenEntry(entry);
                e.Handled = true;
                break;
            case Key.Delete when _vm.DeleteCommand.CanExecute(null):
                _vm.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2 when _vm.EditCommand.CanExecute(null):
                _vm.EditCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.M when Keyboard.Modifiers == ModifierKeys.Control && _vm.MoveToCommand.CanExecute(null):
                _vm.MoveToCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ----- drag and drop: move entries and folders between folders -----

    private System.Windows.Point _dragStart;
    private TreeNodeViewModel? _dragNode;
    private TreeNodeViewModel? _dropHighlight;
    private TreeNodeViewModel? _hoverFolder;
    private readonly System.Windows.Threading.DispatcherTimer _expandTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private const string DragFormat = "RemoteConnectionManager.TreeNode";

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only a press on an item's name starts a drag; the expander and scroll bar keep working as usual.
        _dragNode = e.OriginalSource is DependencyObject source && IsInsideHeader(source) ? ItemAt(source)?.DataContext as TreeNodeViewModel : null;
        _dragStart = e.GetPosition(Tree);
    }

    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is not { } node || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragNode = null;
        if (!_vm.CanStartMove(node)) return;
        try
        {
            DragDrop.DoDragDrop(Tree, new DataObject(DragFormat, node), DragDropEffects.Move);
        }
        finally
        {
            EndDragFeedback();
        }
    }

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        AutoScroll(e);
        if (e.Data.GetData(DragFormat) is not TreeNodeViewModel node)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        var (target, highlight, hoverFolder) = DropTargetAt(e.OriginalSource as DependencyObject);
        var allowed = _vm.CanMove(node, target);
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        SetDropHighlight(allowed ? highlight : null);

        // Hovering on a closed folder opens it after a moment, so deep folders can be reached.
        if (!ReferenceEquals(hoverFolder, _hoverFolder))
        {
            _hoverFolder = hoverFolder;
            _expandTimer.Stop();
            if (hoverFolder is { IsExpanded: false, Children.Count: > 0 }) _expandTimer.Start();
        }
    }

    private void Tree_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires moving between items; only clear when the pointer really left the tree.
        var p = e.GetPosition(Tree);
        if (p.X < 0 || p.Y < 0 || p.X >= Tree.ActualWidth || p.Y >= Tree.ActualHeight) EndDragFeedback();
    }

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var node = e.Data.GetData(DragFormat) as TreeNodeViewModel;
        var (target, _, _) = DropTargetAt(e.OriginalSource as DependencyObject);
        EndDragFeedback();
        if (node is null || !_vm.CanMove(node, target)) return;
        // Folders carry everything inside them (and, in the shared database, their permissions change), so ask first.
        if (node.Folder is { } folder &&
            !_vm.ConfirmMove($"Move folder '{folder.Name}' and everything in it to {(target is null ? "the top level" : $"'{target.Name}'")}?"))
            return;
        _vm.Move(node, target);
    }

    /// <summary>
    /// Where a drop lands: on a folder, into it; on an entry, into the entry's folder; on empty space, the top level.
    /// Also returns the folder node to highlight and the folder under the pointer (for opening on hover).
    /// </summary>
    private (Core.Models.ConnectionFolder? Target, TreeNodeViewModel? Highlight, TreeNodeViewModel? HoverFolder) DropTargetAt(DependencyObject? source)
    {
        if (source is null || ItemAt(source)?.DataContext is not TreeNodeViewModel over) return (null, null, null);
        if (over.IsFolder) return (over.Folder, over, over);
        return (over.Parent?.Folder, over.Parent, null);
    }

    private void SetDropHighlight(TreeNodeViewModel? node)
    {
        if (ReferenceEquals(node, _dropHighlight)) return;
        if (_dropHighlight is not null) _dropHighlight.IsDropTarget = false;
        _dropHighlight = node;
        if (node is not null) node.IsDropTarget = true;
    }

    private void EndDragFeedback()
    {
        SetDropHighlight(null);
        _expandTimer.Stop();
        _hoverFolder = null;
    }

    private void ExpandTimer_Tick(object? sender, EventArgs e)
    {
        _expandTimer.Stop();
        if (_hoverFolder is not null) _hoverFolder.IsExpanded = true;
    }

    /// <summary>Scrolls the tree while dragging near its top or bottom edge.</summary>
    private void AutoScroll(DragEventArgs e)
    {
        if (FindChild<ScrollViewer>(Tree) is not { } scroll) return;
        const double edge = 24;
        var y = e.GetPosition(scroll).Y;
        if (y < edge) scroll.LineUp();
        else if (y > scroll.ActualHeight - edge) scroll.LineDown();
    }

    private static TreeViewItem? ItemAt(DependencyObject source)
    {
        var d = source;
        while (d is not null and not TreeViewItem)
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Double clicks on the expander or empty tree space should not connect.</summary>
    private static bool IsInsideHeader(DependencyObject source)
    {
        for (var d = source; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ToggleButton) return false;
            if (d is ContentPresenter { Name: "PART_Header" }) return true;
            if (d is TreeViewItem) return false;
        }
        return false;
    }

    // ----- command bar -----

    private void DropDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void Rail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (Rail.SelectedIndex)
        {
            case 0:
                if (!_vm.NavigationVisible) _vm.NavigationVisible = true;
                break;
            case 1:
                _vm.SelectedTab = _vm.Dashboard;
                break;
            case 2:
                _vm.FilterText = _vm.FilterText == "favourite" ? string.Empty : "favourite";
                break;
        }
    }

    private void AzureAccount_Click(object sender, MouseButtonEventArgs e) => _vm.OpenAzureBrowserCommand.Execute(null);

    private void GeneratePassword_Click(object sender, RoutedEventArgs e) =>
        PasswordGeneratorPanel.Show(GeneratePasswordButton, "Copy", _vm.CopyGeneratedPassword);

    private void QuickBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.QuickConnectCommand.Execute(null);
            e.Handled = true;
        }
    }
}
