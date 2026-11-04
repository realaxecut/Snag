using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Snag.Services;
using Snag.ViewModels;

namespace Snag.Views;

public partial class PlaylistsView : UserControl
{
    private PlaylistsViewModel? _vm;

    public PlaylistsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.RenameRequested -= OnRenameRequested;
            _vm = (DataContext as MainViewModel)?.Playlists;
            if (_vm is not null) _vm.RenameRequested += OnRenameRequested;
        };
    }

    /// <summary>A new playlist: put the cursor in its name, ready to type over "Playlist 3".</summary>
    private void OnRenameRequested() => Dispatcher.BeginInvoke(() =>
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }, DispatcherPriority.Input);

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add to playlist",
            Multiselect = true,
            Filter = MediaFiles.DialogFilter,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _vm?.AddFiles(dialog.FileNames);
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _vm?.AddFiles(paths);
        e.Handled = true;
    }
}
