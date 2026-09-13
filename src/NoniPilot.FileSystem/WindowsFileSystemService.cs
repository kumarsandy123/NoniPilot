using NoniPilot.Domain.Interfaces;
using NoniPilot.Domain.Security;

namespace NoniPilot.FileSystem;

/// <summary>
/// Real file/folder operations backed by System.IO. Deletes go to the Recycle Bin by
/// default (section 10) and every path-mutating method refuses to touch a protected path
/// even if a caller somehow skipped the policy gate - defense in depth, not a substitute
/// for IPolicyService.
/// </summary>
public sealed class WindowsFileSystemService : IFileSystemService
{
    public Task<IReadOnlyList<FileSystemEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        IReadOnlyList<FileSystemEntry> entries = dir.EnumerateFileSystemInfos()
            .Select(ToEntry)
            .ToList();

        return Task.FromResult(entries);
    }

    public Task<IReadOnlyList<FileSystemEntry>> SearchAsync(string rootPath, FileSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        var dir = new DirectoryInfo(rootPath);
        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Directory not found: {rootPath}");
        }

        var searchOption = criteria.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var pattern = string.IsNullOrWhiteSpace(criteria.NamePattern) ? "*" : criteria.NamePattern;

        IEnumerable<FileInfo> files = dir.EnumerateFiles(pattern, searchOption);

        if (!string.IsNullOrWhiteSpace(criteria.Extension))
        {
            var ext = criteria.Extension.StartsWith('.') ? criteria.Extension : $".{criteria.Extension}";
            files = files.Where(f => string.Equals(f.Extension, ext, StringComparison.OrdinalIgnoreCase));
        }

        if (criteria.MinSizeBytes is { } min)
        {
            files = files.Where(f => f.Length >= min);
        }

        if (criteria.MaxSizeBytes is { } max)
        {
            files = files.Where(f => f.Length <= max);
        }

        if (criteria.ModifiedAfter is { } after)
        {
            files = files.Where(f => f.LastWriteTimeUtc >= after.UtcDateTime);
        }

        IReadOnlyList<FileSystemEntry> result = files.Select(ToEntry).ToList();
        return Task.FromResult(result);
    }

    public Task<string> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        GuardNotProtected(path);
        var created = Directory.CreateDirectory(path);
        return Task.FromResult(created.FullName);
    }

    public Task<string> CreateFileAsync(string path, CancellationToken cancellationToken = default)
    {
        GuardNotProtected(path);

        if (File.Exists(path))
        {
            throw new IOException($"'{path}' already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.Create(path).Dispose();
        return Task.FromResult(path);
    }

    public Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        GuardNotProtected(destinationPath);

        if (Directory.Exists(sourcePath))
        {
            CopyDirectoryRecursive(sourcePath, destinationPath);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        GuardNotProtected(sourcePath);
        GuardNotProtected(destinationPath);

        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, destinationPath);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
            File.Move(sourcePath, destinationPath);
        }

        return Task.CompletedTask;
    }

    public Task RenameAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        GuardNotProtected(path);
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("Path has no parent directory.", nameof(path));
        var destination = Path.Combine(directory, newName);
        GuardNotProtected(destination);

        if (Directory.Exists(path))
        {
            Directory.Move(path, destination);
        }
        else
        {
            File.Move(path, destination);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool useRecycleBin = true, CancellationToken cancellationToken = default)
    {
        GuardNotProtected(path);

        if (!Directory.Exists(path) && !File.Exists(path))
        {
            throw new FileNotFoundException($"Path not found: {path}");
        }

        RecycleBin.Delete(path, useRecycleBin);
        return Task.CompletedTask;
    }

    public bool IsProtectedPath(string path) => ProtectedPaths.IsProtected(path);

    private void GuardNotProtected(string path)
    {
        if (IsProtectedPath(path))
        {
            throw new UnauthorizedAccessException($"'{path}' is a protected system path and cannot be modified.");
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectoryRecursive(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
        }
    }

    private static FileSystemEntry ToEntry(FileSystemInfo info) => new(
        FullPath: info.FullName,
        Name: info.Name,
        IsDirectory: info is DirectoryInfo,
        SizeBytes: info is FileInfo fi ? fi.Length : 0,
        LastModifiedUtc: info.LastWriteTimeUtc);
}
