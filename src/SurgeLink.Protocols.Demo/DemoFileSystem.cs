using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Demo;

/// <summary>In-memory POSIX-like file system backing the demo session's SFTP tab.</summary>
public sealed class DemoFileSystem : IRemoteFileSystem
{
    private sealed class Node
    {
        public required string Name { get; init; }

        public bool IsDirectory { get; init; }

        public byte[] Content { get; set; } = Array.Empty<byte>();

        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

        public int UnixMode { get; set; }
    }

    private readonly object _gate = new();
    private readonly Node _root = new() { Name = "/", IsDirectory = true, UnixMode = 0x1ED };
    private string _current = "/home/demo";

    /// <summary>Creates a demo tree with a handful of files and folders.</summary>
    public DemoFileSystem()
    {
        var home = Mkdir(_root, "home");
        var demo = Mkdir(home, "demo");
        var documents = Mkdir(demo, "Documents");
        var projects = Mkdir(demo, "Projects");
        var surgeLink = Mkdir(projects, "surgelink");
        Write(demo, "demo.txt", "Hello from the SurgeLink demo file system.\n", 0x1A4);
        Write(demo, ".bashrc", "alias ll='ls -al'\n", 0x1A4);
        Write(documents, "notes.txt", "Meeting notes\n- terminal\n- sftp\n- monitor\n", 0x1A4);
        Write(documents, "report.md", "# Q4 report\n\nNumbers look good.\n", 0x1A4);
        Write(surgeLink, "README.md", "# SurgeLink\n", 0x1A4);
    }

    /// <inheritdoc />
    public bool SupportsPermissions => true;

    /// <inheritdoc />
    public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult("/home/demo");

    /// <inheritdoc />
    public Task<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult(_current);

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var node = Resolve(path);
            if (!node.IsDirectory)
            {
                throw new ProtocolException($"'{path}' is not a directory.", ProtocolFailureKind.Configuration);
            }

            var result = node.Children.Values
                .OrderByDescending(n => n.IsDirectory)
                .ThenBy(n => n.Name, StringComparer.Ordinal)
                .Select(n => ToEntry(path, n))
                .ToList();
            return Task.FromResult<IReadOnlyList<RemoteFileEntry>>(result);
        }
    }

    /// <inheritdoc />
    public Task<RemoteFileEntry> StatAsync(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var normalized = PosixPath.Normalize(path, _current);
            var node = Resolve(normalized);
            return Task.FromResult(ToEntry(PosixPath.Parent(normalized), node));
        }
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var node = Resolve(path);
            if (node.IsDirectory)
            {
                throw new ProtocolException($"'{path}' is a directory.", ProtocolFailureKind.Configuration);
            }

            return Task.FromResult<Stream>(new MemoryStream(node.Content, writable: false));
        }
    }

    /// <inheritdoc />
    public Task<Stream> OpenWriteAsync(string path, long? expectedSize, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var normalized = PosixPath.Normalize(path, _current);
        var stream = new UploadStream(this, normalized, expectedSize, progress);
        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc />
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var normalized = PosixPath.Normalize(path, _current);
            var parent = Resolve(PosixPath.Parent(normalized));
            var name = PosixPath.Name(normalized);
            if (parent.Children.ContainsKey(name))
            {
                throw new ProtocolException($"'{path}' already exists.", ProtocolFailureKind.Configuration);
            }

            parent.Children[name] = new Node { Name = name, IsDirectory = true, UnixMode = 0x1ED };
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var normalized = PosixPath.Normalize(path, _current);
            var parent = Resolve(PosixPath.Parent(normalized));
            var name = PosixPath.Name(normalized);
            if (!parent.Children.TryGetValue(name, out var node))
            {
                throw new ProtocolException($"'{path}' does not exist.", ProtocolFailureKind.Configuration);
            }

            if (node.IsDirectory && node.Children.Count > 0 && !recursive)
            {
                throw new ProtocolException($"'{path}' is not empty.", ProtocolFailureKind.Configuration);
            }

            parent.Children.Remove(name);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var source = PosixPath.Normalize(sourcePath, _current);
            var destination = PosixPath.Normalize(destinationPath, _current);
            var sourceParent = Resolve(PosixPath.Parent(source));
            var name = PosixPath.Name(source);
            if (!sourceParent.Children.TryGetValue(name, out var node))
            {
                throw new ProtocolException($"'{sourcePath}' does not exist.", ProtocolFailureKind.Configuration);
            }

            var destinationParent = Resolve(PosixPath.Parent(destination));
            sourceParent.Children.Remove(name);
            var moved = new Node
            {
                Name = PosixPath.Name(destination),
                IsDirectory = node.IsDirectory,
                Content = node.Content,
                ModifiedUtc = DateTimeOffset.UtcNow,
                UnixMode = node.UnixMode,
            };
            foreach (var child in node.Children)
            {
                moved.Children[child.Key] = child.Value;
            }

            destinationParent.Children[moved.Name] = moved;
            return Task.CompletedTask;
        }
    }

    public Task CommitWriteAsync(string temporaryPath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var destination = PosixPath.Normalize(destinationPath, _current);
            var parent = Resolve(PosixPath.Parent(destination));
            var name = PosixPath.Name(destination);
            if (parent.Children.TryGetValue(name, out var existing) && (!overwrite || existing.IsDirectory))
            {
                throw new IOException("Destination already exists or is a directory.");
            }
            return RenameAsync(temporaryPath, destination, cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Resolve(path).UnixMode = unixMode & 0xFFF;
            return Task.CompletedTask;
        }
    }

    private RemoteFileEntry ToEntry(string directory, Node node)
    {
        var full = directory.TrimEnd('/') + "/" + node.Name;
        if (directory == "/")
        {
            full = "/" + node.Name;
        }

        return new RemoteFileEntry
        {
            Name = node.Name,
            FullPath = full,
            Kind = node.IsDirectory ? RemoteEntryKind.Directory : RemoteEntryKind.File,
            Size = node.IsDirectory ? 0 : node.Content.Length,
            ModifiedUtc = node.ModifiedUtc,
            UnixMode = node.UnixMode,
            Owner = "demo",
            Group = "demo",
        };
    }

    private Node Resolve(string path)
    {
        var normalized = PosixPath.Normalize(path, _current);
        var node = _root;
        if (normalized == "/")
        {
            return node;
        }

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Children.TryGetValue(segment, out var child))
            {
                throw new ProtocolException($"No such file or directory: '{path}'.", ProtocolFailureKind.Configuration);
            }

            node = child;
        }

        return node;
    }

    private static Node Mkdir(Node parent, params string[] segments)
    {
        var node = parent;
        foreach (var segment in segments)
        {
            if (!node.Children.TryGetValue(segment, out var child))
            {
                child = new Node { Name = segment, IsDirectory = true, UnixMode = 0x1ED };
                node.Children[segment] = child;
            }

            node = child;
        }

        return node;
    }

    private static void Write(Node directory, string name, string content, int mode) =>
        directory.Children[name] = new Node
        {
            Name = name,
            IsDirectory = false,
            Content = Encoding.UTF8.GetBytes(content),
            UnixMode = mode,
        };

    private sealed class UploadStream : MemoryStream
    {
        private readonly DemoFileSystem _owner;
        private readonly string _path;
        private readonly long? _expected;
        private readonly IProgress<TransferProgress>? _progress;

        public UploadStream(DemoFileSystem owner, string path, long? expected, IProgress<TransferProgress>? progress)
        {
            _owner = owner;
            _path = path;
            _expected = expected;
            _progress = progress;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var bytes = ToArray();
                lock (_owner._gate)
                {
                    var parent = _owner.Resolve(PosixPath.Parent(_path));
                    parent.Children[PosixPath.Name(_path)] = new Node
                    {
                        Name = PosixPath.Name(_path),
                        IsDirectory = false,
                        Content = bytes,
                        ModifiedUtc = DateTimeOffset.UtcNow,
                        UnixMode = 0x1A4,
                    };
                }

                _progress?.Report(new TransferProgress(bytes.Length, _expected ?? bytes.Length, _path));
            }

            base.Dispose(disposing);
        }
    }
}
