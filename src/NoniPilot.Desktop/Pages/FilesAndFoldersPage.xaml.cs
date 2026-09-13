using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NoniPilot.Desktop.Services;
using NoniPilot.Domain.Interfaces;

namespace NoniPilot.Desktop.Pages;

public partial class FilesAndFoldersPage : UserControl
{
    private readonly AppServices _services;
    private string _currentPath;

    private sealed record FileEntryRow(string FullPath, string Name, bool IsDirectory, string TypeLabel, string SizeLabel, string ModifiedLabel);

    public FilesAndFoldersPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _currentPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        PathBox.Text = _currentPath;
        _ = LoadDirectoryAsync(_currentPath);
    }

    private async Task LoadDirectoryAsync(string path)
    {
        try
        {
            var entries = await _services.FileSystem.ListDirectoryAsync(path);
            EntriesList.ItemsSource = entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToRow)
                .ToList();
            _currentPath = path;
            PathBox.Text = path;
            StatusText.Text = $"{entries.Count} item(s) in {path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open '{path}': {ex.Message}";
        }
    }

    private static FileEntryRow ToRow(FileSystemEntry e) => new(
        e.FullPath, e.Name, e.IsDirectory,
        e.IsDirectory ? "Folder" : "File",
        e.IsDirectory ? "" : FormatSize(e.SizeBytes),
        e.LastModifiedUtc.ToLocalTime().ToString("g"));

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
    };

    private void Go_Click(object sender, RoutedEventArgs e) => _ = LoadDirectoryAsync(PathBox.Text.Trim());

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = LoadDirectoryAsync(PathBox.Text.Trim());
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadDirectoryAsync(_currentPath);

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_currentPath);
        if (parent is not null)
        {
            _ = LoadDirectoryAsync(parent.FullName);
        }
    }

    private void EntriesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EntriesList.SelectedItem is not FileEntryRow row)
        {
            return;
        }

        if (row.IsDirectory)
        {
            _ = LoadDirectoryAsync(row.FullPath);
        }
        else
        {
            _ = RunAsync("Application", "Launch", new Dictionary<string, object?> { ["appPathOrName"] = row.FullPath },
                ct => _services.Applications.LaunchAsync(row.FullPath, null, ct));
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not FileEntryRow row || string.IsNullOrWhiteSpace(ActionNameBox.Text))
        {
            StatusText.Text = "Select an item and enter a new name first.";
            return;
        }

        var newName = ActionNameBox.Text.Trim();
        _ = RunAsync("FileSystem", "Rename", new Dictionary<string, object?> { ["path"] = row.FullPath, ["newName"] = newName },
            ct => _services.FileSystem.RenameAsync(row.FullPath, newName, ct));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not FileEntryRow row || string.IsNullOrWhiteSpace(ActionNameBox.Text))
        {
            StatusText.Text = "Select an item and enter a destination path first.";
            return;
        }

        var destination = ActionNameBox.Text.Trim();
        _ = RunAsync("FileSystem", "Copy", new Dictionary<string, object?> { ["sourcePath"] = row.FullPath, ["destinationPath"] = destination },
            ct => _services.FileSystem.CopyAsync(row.FullPath, destination, ct));
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not FileEntryRow row || string.IsNullOrWhiteSpace(ActionNameBox.Text))
        {
            StatusText.Text = "Select an item and enter a destination path first.";
            return;
        }

        var destination = ActionNameBox.Text.Trim();
        _ = RunAsync("FileSystem", "Move", new Dictionary<string, object?> { ["sourcePath"] = row.FullPath, ["destinationPath"] = destination },
            ct => _services.FileSystem.MoveAsync(row.FullPath, destination, ct), reloadAfter: true);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not FileEntryRow row)
        {
            StatusText.Text = "Select an item to delete first.";
            return;
        }

        _ = RunAsync("FileSystem", "Delete", new Dictionary<string, object?> { ["path"] = row.FullPath },
            ct => _services.FileSystem.DeleteAsync(row.FullPath, useRecycleBin: true, ct), reloadAfter: true);
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewNameBox.Text))
        {
            StatusText.Text = "Enter a file name first.";
            return;
        }

        var path = Path.Combine(_currentPath, NewNameBox.Text.Trim());
        _ = RunAsync("FileSystem", "CreateFile", new Dictionary<string, object?> { ["path"] = path },
            ct => _services.FileSystem.CreateFileAsync(path, ct), reloadAfter: true);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewNameBox.Text))
        {
            StatusText.Text = "Enter a folder name first.";
            return;
        }

        var path = Path.Combine(_currentPath, NewNameBox.Text.Trim());
        _ = RunAsync("FileSystem", "CreateDirectory", new Dictionary<string, object?> { ["path"] = path },
            ct => _services.FileSystem.CreateDirectoryAsync(path, ct), reloadAfter: true);
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var criteria = new FileSearchCriteria
            {
                NamePattern = string.IsNullOrWhiteSpace(SearchPatternBox.Text) ? null : SearchPatternBox.Text.Trim(),
                Extension = string.IsNullOrWhiteSpace(SearchExtensionBox.Text) ? null : SearchExtensionBox.Text.Trim(),
                Recursive = RecursiveCheckBox.IsChecked == true,
            };

            var results = await _services.FileSystem.SearchAsync(_currentPath, criteria);
            EntriesList.ItemsSource = results.Select(ToRow).ToList();
            StatusText.Text = $"Found {results.Count} item(s) under {_currentPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search failed: {ex.Message}";
        }
    }

    private async Task RunAsync(string tool, string action, IReadOnlyDictionary<string, object?> parameters, Func<CancellationToken, Task> execute, bool reloadAfter = false)
    {
        var (success, message) = await _services.ActionRunner.RunAsync(tool, action, parameters, execute);
        StatusText.Text = message;

        if (success && reloadAfter)
        {
            await LoadDirectoryAsync(_currentPath);
        }
    }
}
