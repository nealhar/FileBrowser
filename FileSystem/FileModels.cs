namespace FileBrowser.FileSystem;

public sealed record FileBrowserOptions
{
    public string HomeDirectory { get; init; } = "FileBrowserData";
    public int MaximumSearchResults { get; init; } = 500;
    public long MaximumUploadBytes { get; init; } = 100 * 1024 * 1024;
}

public sealed record FileEntry(string Name, string RelativePath, bool IsDirectory,
    long? SizeBytes, DateTime LastModifiedUtc);

public sealed record BrowseResult(string CurrentPath, IReadOnlyList<FileEntry> Items,
    int FileCount, int FolderCount, long TotalFileSizeBytes);

public sealed record SearchResult(IReadOnlyList<FileEntry> Items, int FileCount,
    int FolderCount, long TotalFileSizeBytes, bool LimitReached);

public sealed record TransferRequest(string SourcePath, string DestinationPath,
    bool Overwrite = false);

public sealed record CreateFolderRequest(string ParentPath, string Name);

public sealed class InvalidPathException(string message) : ArgumentException(message);
public sealed class DestinationExistsException(string message) : IOException(message);
