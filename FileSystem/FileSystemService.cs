using Microsoft.AspNetCore.Http;

namespace FileBrowser.FileSystem;

public sealed class FileSystemService
{
    // 64 Kilobyte buffer
    private const int BufferSize = 64 * 1024;
    private readonly string _root;
    private readonly string _rootPath;
    // Path comparison mode for compatibilty between linux and windows
    private readonly StringComparison _comparison;

    // construct and initializes the service  
    public FileSystemService(string configuredRoot, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException("FileBrowser:HomeDirectory is required.");

        var path = Path.IsPathRooted(configuredRoot)
            ? configuredRoot
            : Path.Combine(contentRoot, configuredRoot);
        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        _root = Path.EndsInDirectorySeparator(_rootPath)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        _comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    // converts client relative path to safe absolute server path
    public string ResolvePath(string? relativePath)
    {
        relativePath ??= "";
        if (relativePath.Contains('\0') || Path.IsPathRooted(relativePath) ||
            LooksAbsoluteOnAnotherPlatform(relativePath))
            throw new InvalidPathException("Absolute or malformed paths are not allowed.");

        var normalized = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(_root, normalized)));

        if (!fullPath.Equals(_rootPath, _comparison) &&
            !fullPath.StartsWith(_root, _comparison))
            throw new InvalidPathException("The requested path is outside the home directory.");

        RejectReparsePoints(fullPath);
        return fullPath;
    }

    // returns immediate content of one directory
    public BrowseResult Browse(string? relativePath)
    {
        var directory = ExistingDirectory(relativePath, "The requested folder does not exist.");
        var items = new List<FileEntry>();
        var files = 0;
        var folders = 0;
        long bytes = 0;

        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var item = CreateEntry(path);
            items.Add(item);
            Count(item, ref files, ref folders, ref bytes);
        }

        items.Sort(CompareEntries);
        return new BrowseResult(RelativePath(directory), items, files, folders, bytes);
    }

    // Recursively search file and folder names beneath a base directory
    public SearchResult Search(string? relativePath, string query, int limit,
        CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidPathException("A search query is required.");
        query = query.Trim();

        var root = ExistingDirectory(relativePath, "The search folder does not exist.");
        var pending = new Stack<string>();

        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        // Pre allocate a result list with max capacity of 256 to avoid a large array immediately
        var items = new List<FileEntry>(Math.Min(limit, 256));
        pending.Push(root);
        var files = 0;
        var folders = 0;
        long bytes = 0;

        /* Search traversal - continues searching while there are unsearched directories 
            and while the result limit has not been reached*/
        while (pending.Count > 0 && items.Count < limit)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    cancellation.ThrowIfCancellationRequested();
                    // Catches access failures such as Permissino Denied, File deleted during search, etc
                    if (!TryAttributes(path, out var attributes)) continue;

                    if (IsSearchableDirectory(attributes)) pending.Push(path);
                    // For case-insensitive substring match
                    if (!Path.GetFileName(path).Contains(query,
                        StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        var item = CreateEntry(path, attributes);
                        items.Add(item);
                        Count(item, ref files, ref folders, ref bytes);
                    }
                    // Entry changed or became inaccessible
                    catch (Exception error) when (IsSkippable(error)) { }

                    if (items.Count >= limit) break;
                }
            }
            // Directory came or became inaccessible
            catch (Exception error) when (IsSkippable(error)) { }
        }

        items.Sort(CompareEntries);
        return new SearchResult(items, files, folders, bytes, items.Count == limit);
    }

    // Opens a validated file for download, returns stream so memory use does not grow with file size
    public FileStream OpenDownload(string relativePath)
    {
        var path = ResolvePath(relativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("The requested file does not exist.");
        return ReadStream(path);
    }

    // Asynchronously uploads one or more browser-provided files
    public async Task UploadAsync(string? relativePath, IFormFileCollection uploads,
        bool overwrite, long maximumBytes, CancellationToken cancellation)
    {
        var directory = ExistingDirectory(relativePath, "The upload folder does not exist.");
        if (uploads.Count == 0) throw new InvalidDataException("Choose a file to upload.");

        long total = 0;
        foreach (var upload in uploads)
        {
            cancellation.ThrowIfCancellationRequested();
            if (upload.Length > maximumBytes - total)
                throw new BadHttpRequestException("The upload is too large.", Statuscodes.Status413PayloadTooLarge);
            total += upload.Length;

            var name = ValidateName(Path.GetFileName(upload.FileName.Replace('\\', '/')));
            var destination = Path.Combine(directory, name);
            if (!overwrite && Exists(destination))
                throw new DestinationExistsException($"An item named '{name}' already exists.");

            /* Creates temporary unique filename so partial uploads do not  appear under final filename
                and so failed uploads can be deleted safely, etc*/
            var temporary = Path.Combine(directory, $".{name}.uploading-{Guid.NewGuid():N}");
            try
            {
                // streams upload into temporary file, the whole file is not loaded into
                await using (var output = WriteStream(temporary))
                    await upload.CopyToAsync(output, cancellation);
                File.Move(temporary, destination, overwrite);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    // Creates one directory under an existing parent
    public void CreateFolder(string? parentPath, string name)
    {
        var parent = ExistingDirectory(parentPath, "The parent folder does not exist.");
        var destination = Path.Combine(parent, ValidateName(name));
        EnsureAvailable(destination);
        Directory.CreateDirectory(destination);
    }

    // Deletes either a file or a folder
    public void Delete(string relativePath, bool recursive)
    {
        var path = ResolvePath(relativePath);
        EnsureNotRoot(path, "deleted");
        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }
        if (!Directory.Exists(path))
            throw new FileNotFoundException("The requested item does not exist.");
        if (!recursive && Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException("The folder is not empty. Enable recursive deletion.");
        Directory.Delete(path, recursive);
    }

    // moves or renames a file or folder
    public void Move(TransferRequest request)
    {
        var source = ResolvePath(request.SourcePath);
        var destination = ResolvePath(request.DestinationPath);
        EnsureSource(source);
        EnsureNotRoot(source, "moved");
        EnsureParent(destination);
        EnsureNotInsideItself(source, destination);

        if (File.Exists(source))
        {
            if (Directory.Exists(destination))
                throw new DestinationExistsException("A folder exists at the destination.");
            File.Move(source, destination, request.Overwrite);
            return;
        }

        EnsureAvailable(destination);
        Directory.Move(source, destination);
    }

    // asynchronously copies either one file or a directory tree
    public async Task CopyAsync(TransferRequest request, CancellationToken cancellation)
    {
        var source = ResolvePath(request.SourcePath);
        var destination = ResolvePath(request.DestinationPath);
        EnsureSource(source);
        EnsureParent(destination);
        EnsureNotInsideItself(source, destination);

        if (File.Exists(source))
        {
            if (Directory.Exists(destination) || (!request.Overwrite && File.Exists(destination)))
                throw new DestinationExistsException("An item exists at the destination.");
            await CopyFileAsync(source, destination, request.Overwrite, cancellation);
            return;
        }

        EnsureAvailable(destination);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        try
        {
            while (pending.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var current = pending.Pop();
                Directory.CreateDirectory(current.Destination);

                foreach (var child in Directory.EnumerateFileSystemEntries(current.Source))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(child);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    var target = Path.Combine(current.Destination, Path.GetFileName(child));
                    if (attributes.HasFlag(FileAttributes.Directory)) pending.Push((child, target));
                    else await CopyFileAsync(child, target, false, cancellation);
                }
            }
        }
        // If error then remove partially copied tree
        catch
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            throw;
        }
    }

    // Helper to copy one file safely
    private async Task CopyFileAsync(string source, string destination, bool overwrite,
        CancellationToken cancellation)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.copying-{Guid.NewGuid():N}");
        await using var input = ReadStream(source);
        try
        {
            await using (var output = WriteStream(temporary))
                await input.CopyToAsync(output, BufferSize, cancellation);
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // converts filesystem path into API's FileEntry model
    private FileEntry CreateEntry(string path, FileAttributes? knownAttributes = null)
    {
        var isDirectory = (knownAttributes ?? File.GetAttributes(path))
            .HasFlag(FileAttributes.Directory);
        if (isDirectory)
        {
            var info = new DirectoryInfo(path);
            return new(info.Name, RelativePath(path), true, null, info.LastWriteTimeUtc);
        }
        var file = new FileInfo(path);
        return new(file.Name, RelativePath(path), false, file.Length, file.LastWriteTimeUtc);
    }

    // Combines safe path resolution and directory existence checking
    private string ExistingDirectory(string? relativePath, string message)
    {
        var path = ResolvePath(relativePath);
        return Directory.Exists(path) ? path : throw new DirectoryNotFoundException(message);
    }

    // Helper for converting absolute server path into API relative path
    private string RelativePath(string path) => path.Equals(_rootPath, _comparison)
        ? ""
        : Path.GetRelativePath(_rootPath, path).Replace(Path.DirectorySeparatorChar, '/');

    // Validates one filename or folder name
    private static string ValidateName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\'))
            throw new InvalidPathException("The item name is invalid.");
        return name;
    }

    // Prevents destructive operations against configured home
    private void EnsureNotRoot(string path, string operation)
    {
        if (path.Equals(_rootPath, _comparison))
            throw new InvalidPathException($"The home folder cannot be {operation}.");
    }

    // Confirms a move or copy source exists
    private static void EnsureSource(string path)
    {
        if (!Exists(path)) throw new FileNotFoundException("The source item does not exist.");
    }

    // Confirms that destinations parent directory exists
    private static void EnsureParent(string path)
    {
        if (!Directory.Exists(Path.GetDirectoryName(path)))
            throw new DirectoryNotFoundException("The destination folder does not exist.");
    }

    // Requires that no item already occupies the destination
    private static void EnsureAvailable(string path)
    {
        if (Exists(path))
            throw new DestinationExistsException("An item exists at the destination.");
    }

    // Prevents moving or copying a directory into itself
    private void EnsureNotInsideItself(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        var boundary = Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar;
        if (destination.Equals(source, _comparison) || destination.StartsWith(boundary, _comparison))
            throw new InvalidPathException("A folder cannot be moved or copied inside itself.");
    }

    // Rejects reparse points between root and target
    private void RejectReparsePoints(string fullPath)
    {
        var relative = Path.GetRelativePath(_rootPath, fullPath);
        if (relative == ".") return;

        var current = _rootPath;
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Exists(current) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidPathException("Symbolic links are not accessible.");
        }
    }

    // Helper for recognizing the absolute path formats
    private static bool LooksAbsoluteOnAnotherPlatform(string path) =>
        path.StartsWith('/') || path.StartsWith('\\') ||
        (path.Length > 1 && char.IsLetter(path[0]) && path[1] == ':');

    // Attempts to retrieve filesystem attributes
    private static bool TryAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (Exception error) when (IsSkippable(error))
        { attributes = default; return false; }
    }

    // Determines whether a search should recurse into an entry
    private static bool IsSearchableDirectory(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.Directory) &&
        (attributes & (FileAttributes.Hidden | FileAttributes.System |
            FileAttributes.ReparsePoint)) == 0;

    // Defines failures that search may ignore
    private static bool IsSkippable(Exception error) =>
        error is IOException or UnauthorizedAccessException;

    // Determines when a file or directory exists at the path
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    // Defines ReadStream for downloads and file copies
    private static FileStream ReadStream(string path) => new(path, FileMode.Open,
        FileAccess.Read, FileShare.Read, BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    // Defines WriteStream for writing
    private static FileStream WriteStream(string path) => new(path, FileMode.CreateNew,
        FileAccess.Write, FileShare.None, BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    // Updates summary counters for one entry
    private static void Count(FileEntry item, ref int files, ref int folders, ref long bytes)
    {
        if (item.IsDirectory) folders++;
        else { files++; bytes += item.SizeBytes ?? 0; }
    }

    // Defines how two entries should be ordered when sorting
    private static int CompareEntries(FileEntry left, FileEntry right)
    {
        var type = right.IsDirectory.CompareTo(left.IsDirectory);
        return type != 0 ? type : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    }
}
