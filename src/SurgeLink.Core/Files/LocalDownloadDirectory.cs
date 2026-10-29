using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SurgeLink.Core.Files;

/// <summary>Pins Windows destination ancestors without following reparse points while a download is written and published.</summary>
internal sealed partial class LocalDownloadDirectory : IDisposable
{
    private readonly List<SafeFileHandle> _directories = [];

    internal static IDisposable Open(string path)
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
                Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows())
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Download directories must not contain symbolic links.");
                    continue;
                }
                var nativePath = directory.StartsWith(@"\\?\", StringComparison.Ordinal) ? directory
                    : directory.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + directory[2..] : @"\\?\" + directory;
                // Deny write/delete sharing: an ancestor cannot be renamed or
                // converted to a junction while it owns a download destination.
                var handle = CreateFile(nativePath, 0x80, FileShare.Read, 0, FileMode.Open, 0x02200000, 0);
                lease._directories.Add(handle);
                if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, 8))
                    throw new IOException("Cannot secure the download directory.", new Win32Exception(Marshal.GetLastPInvokeError()));
                if (((FileAttributes)info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new IOException("Download directories must not contain junctions or symbolic links.");
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

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
