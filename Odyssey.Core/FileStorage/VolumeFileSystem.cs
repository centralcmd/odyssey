namespace Odyssey.Core.FileStorage;

/// <summary>
/// The filesystem primitives every store operation goes through (issue #346 §7.5): composition with a
/// containment check, the symlink check on every directory from the feature root down, mode-setting creates,
/// a link-safe recursive delete, and the classification of an I/O failure as a degraded volume.
/// </summary>
/// <remarks>
/// A time-of-check/time-of-use window remains between the symlink check and the open, because .NET exposes no
/// <c>O_NOFOLLOW</c>. Exploiting it requires write access to the volume as the <c>app</c> user, which already
/// grants the data — an accepted residual risk.
/// </remarks>
public static class VolumeFileSystem
{
    /// <summary><c>0640</c>: files are readable by the group, never by others, never executable.</summary>
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    /// <summary><c>0750</c>.</summary>
    public const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    // errno values .NET carries in IOException.HResult on Linux.
    private const int EEXIST = 17;
    private const int ENOSPC = 28;

    /// <summary>
    /// <paramref name="relativePath"/> under <paramref name="rootPath"/>, refusing anything whose full path does not
    /// start with <c>root/expectedTop/</c>. The structural check has already run; this is the second, independent
    /// line, against a path that normalises somewhere else.
    /// </summary>
    public static string Contain(string rootPath, string relativePath, string expectedTop)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root + Path.DirectorySeparatorChar + expectedTop + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal)
            ? full
            : throw new FileStorageIntegrityException("A stored file's path failed the containment check.");
    }

    /// <summary>
    /// Every existing directory from <c>root/top</c> down to <paramref name="fullPath"/>'s folder, and the leaf
    /// itself if it exists, must not be a symbolic link. The volume root itself may be one (the operator's choice).
    /// </summary>
    public static void EnsureNoLinks(string rootPath, string fullPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
            {
                return;
            }

            if (info.LinkTarget is not null)
            {
                throw new FileStorageIntegrityException("A stored file's path crosses a symbolic link.");
            }
        }
    }

    /// <summary>A directory that must exist as a real directory, not a link: <c>.tmp</c>, <c>.quarantine</c>.</summary>
    public static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists && info.LinkTarget is not null;
    }

    /// <summary>
    /// Creates <paramref name="path"/> and any missing parents, each with <c>0750</c>. One level at a time, because
    /// <c>Directory.CreateDirectory(path, mode)</c> applies the mode to the leaf only and leaves every parent it
    /// creates at the process default.
    /// </summary>
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        var missing = new Stack<string>();
        for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
             !string.IsNullOrEmpty(current) && !Directory.Exists(current);
             current = Path.GetDirectoryName(current))
        {
            missing.Push(current);
        }

        while (missing.TryPop(out var directory))
        {
            Directory.CreateDirectory(directory, DirectoryMode);
        }
    }

    /// <summary>A new file — <c>CreateNew</c>, so nothing is ever overwritten — with mode <c>0640</c>.</summary>
    public static FileStream CreateNew(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            BufferSize = 0,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode;
        }

        return new FileStream(path, options);
    }

    /// <summary>The V1 and degraded-volume probe: a write and delete in the root.</summary>
    public static bool TryProbeWrite(string rootPath, out Exception? failure)
    {
        var probe = Path.Combine(rootPath, $".odyssey-probe-{Guid.NewGuid():N}");
        try
        {
            using (var stream = CreateNew(probe))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }

            File.Delete(probe);
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex;
            TryDelete(probe);
            return false;
        }
    }

    /// <summary>
    /// Whether a failure says something about the whole volume (§3.8): the root gone, permission or a read-only
    /// remount, a full disk. A single missing object file is not one of these.
    /// </summary>
    /// <remarks>
    /// Callers pass only failures of the volume's own file operations — never one from reading the caller's
    /// input stream, which says nothing about the disk. "File exists" is a name collision, not a disk fault.
    /// </remarks>
    public static bool IsVolumeFailure(Exception exception) =>
        exception is DirectoryNotFoundException or UnauthorizedAccessException
        || exception is IOException { HResult: not EEXIST } and not FileNotFoundException;

    /// <summary>The target of a no-overwrite create or move already exists.</summary>
    public static bool IsAlreadyExists(Exception exception) =>
        exception is IOException { HResult: EEXIST } && exception is not FileNotFoundException;

    public static bool IsDiskFull(Exception exception) =>
        exception is IOException { HResult: ENOSPC };

    /// <summary>Best effort; an absent file counts as deleted.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the row that owns the path keeps driving a retry.
        }
    }

    /// <summary>
    /// Deletes a tree without following links: a link is deleted as a link and its target survives.
    /// </summary>
    public static void DeleteTreeWithoutFollowingLinks(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
        {
            info.Delete();
            return;
        }

        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null || entry is FileInfo)
            {
                entry.Delete();
            }
            else
            {
                DeleteTreeWithoutFollowingLinks(entry.FullName);
            }
        }

        info.Delete();
    }

    /// <summary>
    /// Removes empty folders from <paramref name="folder"/> upwards, never past <paramref name="stopAt"/> (the
    /// feature root, which stays).
    /// </summary>
    public static void RemoveEmptyFolders(string folder, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        while (current.Length > stop.Length && current.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            try
            {
                var info = new DirectoryInfo(current);
                if (!info.Exists || info.LinkTarget is not null || info.EnumerateFileSystemInfos().Any())
                {
                    return;
                }

                info.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            current = Path.GetDirectoryName(current)!;
        }
    }

    /// <summary>
    /// The real path of an existing directory, resolving a link at every component (C3/C4 compare after link
    /// resolution). A component that does not exist is kept as written.
    /// </summary>
    public static string RealPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            for (var hops = 0; hops < 40; hops++)
            {
                var info = new DirectoryInfo(current);
                if (!info.Exists || info.LinkTarget is not { } target)
                {
                    break;
                }

                current = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(current)!, target)));
            }
        }

        return current;
    }

    /// <summary>Whether two real paths are equal or one lies inside the other.</summary>
    public static bool Overlaps(string a, string b)
    {
        var left = a.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var right = b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal);
    }
}

/// <summary>
/// How the store names a failure in a log line. Never the exception itself: an I/O message embeds the full path,
/// and a database duplicate-key message the key value — both carry a user's file name, which is personal data.
/// </summary>
public static class FileStorageLog
{
    public static string Describe(Exception exception) =>
        $"{exception.GetType().Name} (0x{exception.HResult:X8})";
}
