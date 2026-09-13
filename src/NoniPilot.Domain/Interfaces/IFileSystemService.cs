namespace NoniPilot.Domain.Interfaces;

public sealed record FileSystemEntry(
    string FullPath,
    string Name,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc);

public sealed class FileSearchCriteria
{
    public string? NamePattern { get; init; }
    public string? Extension { get; init; }
    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }
    public DateTimeOffset? ModifiedAfter { get; init; }
    public bool Recursive { get; init; } = true;
}

/// <summary>File/folder operations (section 8.5). Policy-gated by the caller, same convention as IComputerControlService.</summary>
public interface IFileSystemService
{
    Task<IReadOnlyList<FileSystemEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileSystemEntry>> SearchAsync(string rootPath, FileSearchCriteria criteria, CancellationToken cancellationToken = default);

    Task<string> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Creates a new empty file (and any missing parent folders). Fails if the file already exists.</summary>
    Task<string> CreateFileAsync(string path, CancellationToken cancellationToken = default);

    Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    Task RenameAsync(string path, string newName, CancellationToken cancellationToken = default);

    /// <summary>Deletes to the Recycle Bin by default (section 10: "file deletion should prefer Recycle Bin where appropriate").</summary>
    Task DeleteAsync(string path, bool useRecycleBin = true, CancellationToken cancellationToken = default);

    /// <summary>True for OS-critical paths that policy must never allow write/delete access to (section 8.5, 18).</summary>
    bool IsProtectedPath(string path);
}
