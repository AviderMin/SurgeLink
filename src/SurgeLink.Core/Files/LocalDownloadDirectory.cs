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
    private const uint CloudReparseTag = 0x9000001A;
    private const uint CloudReparseTagMask = 0x0000F000;
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
                        throw new IOException("Download directories must not contain symbolic links.");
                    continue;
                }
                var nativePath = ToExtendedPath(directory);
                // Deny write/delete sharing: an ancestor cannot be renamed or
                // converted to a junction while it owns a download destination.
                var handle = CreateFile(nativePath, 0x80, FileShare.Read, 0, FileMode.Open, 0x02200000, 0);
                lease._directories.Add(handle);
                if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, 8))
                    throw new IOException("Cannot secure the download directory.", new Win32Exception(Marshal.GetLastPInvokeError()));
                var attributes = (FileAttributes)info.Attributes;
                if ((attributes & FileAttributes.Directory) == 0
                    || (attributes & FileAttributes.ReparsePoint) != 0 && !IsCloudReparseTag(info.ReparseTag))
                    throw new IOException("Download directories must not contain junctions or symbolic links.");
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static FileStream OpenExistingFileForRead(string path)
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
                throw new IOException("The local upload source must be a regular file.");
            return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous);
        }

        var nativePath = ToExtendedPath(fullPath);
        var handle = CreateFile(nativePath, GenericRead, FileShare.Read, 0, FileMode.Open, OpenReparsePoint | Overlapped, 0);
        try
        {
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, 8))
                throw new IOException("Cannot open the local upload file.", new Win32Exception(Marshal.GetLastPInvokeError()));
            var attributes = (FileAttributes)info.Attributes;
            if ((attributes & FileAttributes.Directory) != 0)
                throw new IOException("The local upload source must be a regular file.");
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (!IsCloudReparseTag(info.ReparseTag))
                    throw new IOException("The local upload source must be a regular file.");

                // Keep the inspected placeholder pinned while opening it for hydration.
                using (handle)
                    return new FileStream(nativePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous);
            }
            return new FileStream(handle, FileAccess.Read, bufferSize, isAsync: true);
        }
        catch { handle.Dispose(); throw; }
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
}
