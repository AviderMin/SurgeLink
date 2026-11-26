using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SurgeLink.Core.Files;

/// <summary>Pins Windows directory ancestors without following reparse points.</summary>
internal sealed partial class LocalDownloadDirectory : IDisposable
{
    private const uint OpenReparsePoint = 0x00200000;
    private const uint Overlapped = 0x40000000;
    private const uint GenericRead = 0x80000000;
    private const uint ListDirectory = 0x00000001;
    private const uint ReadAttributes = 0x00000080;
    private const uint CloudReparseTag = 0x9000001A;
    private const uint CloudReparseTagMask = 0x0000F000;
    private const int FileNotFound = 2;
    private const int PathNotFound = 3;
    private readonly List<SafeFileHandle> _directories = [];

    internal static IDisposable Open(string path, bool create = true)
    {
        var lease = new LocalDownloadDirectory();
        try
        {
            var chain = new Stack<string>();
            for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
                chain.Push(directory.FullName);
            while (chain.TryPop(out var directory))
            {
                // Each parent is already pinned before creating or opening its child.
                if (create) Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows())
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new UnsafeLocalPathException("Download directories must not contain symbolic links.");
                    continue;
                }
                var nativePath = ToExtendedPath(directory);
                // Deny delete sharing so an ancestor cannot be renamed while
                // allowing write access needed to replace files within it.
                var handle = CreateFile(nativePath, ListDirectory | ReadAttributes, FileShare.ReadWrite, 0, FileMode.Open, 0x02200000, 0);
                var openError = Marshal.GetLastPInvokeError();
                lease._directories.Add(handle);
                if (handle.IsInvalid)
                {
                    if (openError is FileNotFound or PathNotFound)
                        throw new DirectoryNotFoundException("The local file directory does not exist.");
                    throw new IOException("Cannot secure the download directory.", new Win32Exception(openError));
                }
                if (!GetFileInformationByHandleEx(handle, 9, out var info, 8))
                    throw new IOException("Cannot secure the download directory.", new Win32Exception(Marshal.GetLastPInvokeError()));
                var attributes = (FileAttributes)info.Attributes;
                if ((attributes & FileAttributes.Directory) == 0
                    || (attributes & FileAttributes.ReparsePoint) != 0 && !IsCloudReparseTag(info.ReparseTag))
                    throw new UnsafeLocalPathException("Download directories must not contain junctions or symbolic links.");
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static FileStream OpenExistingFileForRead(string path, bool rejectHardLinks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        const int bufferSize = 81920;
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("The local upload path must name a file inside a directory.");
        using var parents = Open(parent, create: false);
        if (!OperatingSystem.IsWindows())
        {
            if ((File.GetAttributes(fullPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new UnsafeLocalPathException("The local upload source must be a regular file.");
            return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous);
        }

        var nativePath = ToExtendedPath(fullPath);
        var handle = CreateFile(nativePath, GenericRead, FileShare.Read, 0, FileMode.Open, OpenReparsePoint | Overlapped, 0);
        try
        {
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is FileNotFound or PathNotFound)
                    throw new FileNotFoundException("The local upload file does not exist.", fullPath);
                throw new IOException("Cannot open the local upload file.", new Win32Exception(error));
            }
            if (!GetFileInformationByHandleEx(handle, 9, out var info, 8))
                throw new IOException("Cannot open the local upload file.", new Win32Exception(Marshal.GetLastPInvokeError()));
            ValidateFileType((FileAttributes)info.Attributes, info.ReparseTag);
            if (rejectHardLinks) { RequireSingleLink(handle); }
            if (((FileAttributes)info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Keep the inspected placeholder pinned while opening it for hydration.
                using (handle)
                    return new FileStream(nativePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous);
            }
            return new FileStream(handle, FileAccess.Read, bufferSize, isAsync: true);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static bool IsSafeExistingFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { ValidateFileType(File.GetAttributes(path), 0); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            return true;
        }

        using var handle = CreateFile(ToExtendedPath(path), ReadAttributes, FileShare.ReadWrite | FileShare.Delete,
            0, FileMode.Open, OpenReparsePoint | 0x02000000, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is FileNotFound or PathNotFound) return false;
            throw new IOException("Cannot inspect the local file.", new Win32Exception(error));
        }
        if (!GetFileInformationByHandleEx(handle, 9, out var info, 8))
            throw new IOException("Cannot inspect the local file.", new Win32Exception(Marshal.GetLastPInvokeError()));
        ValidateFileType((FileAttributes)info.Attributes, info.ReparseTag);
        RequireSingleLink(handle);
        return true;
    }

    private static void RequireSingleLink(SafeFileHandle handle)
    {
        const int fileStandardInfo = 1;
        if (!GetFileStandardInfo(handle, fileStandardInfo, out var info, (uint)Marshal.SizeOf<StandardInfo>()))
            throw new IOException("Cannot inspect local file links.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if (info.NumberOfLinks != 1)
            throw new UnsafeLocalPathException("The local file must have exactly one hard link.");
    }

    private static void ValidateFileType(FileAttributes attributes, uint tag)
    {
        if ((attributes & FileAttributes.Directory) != 0
            || (attributes & FileAttributes.ReparsePoint) != 0 && !IsCloudReparseTag(tag))
            throw new UnsafeLocalPathException("The local path must name a regular file or cloud placeholder.");
    }

    private static bool IsCloudReparseTag(uint tag) => (tag & ~CloudReparseTagMask) == CloudReparseTag;

    private static string ToExtendedPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;

    public void Dispose()
    {
        for (var i = _directories.Count - 1; i >= 0; i--) _directories[i].Dispose();
        _directories.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security,
        FileMode disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTagInfo info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileStandardInfo(SafeFileHandle file, int infoClass, out StandardInfo info, uint size);
}
