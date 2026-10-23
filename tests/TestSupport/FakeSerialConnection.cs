using System.Collections.Concurrent;
using SurgeLink.Protocols.Serial;

namespace SurgeLink.Tests.Support;

internal sealed class FakeSerialConnection : ISerialConnection
{
    private readonly BlockingCollection<byte[]> _incoming = new();
    private byte[]? _pending;
    private int _offset;
    private int _disposeCount;
    public Action? OpenAction { get; init; }
    public Action<byte[]>? WriteAction { get; init; }
    public bool Echo { get; init; }
    public Exception? ReadFailure { get; set; }
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public ConcurrentQueue<byte[]> Writes { get; } = new();
    public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() { OpenStarted.TrySetResult(); OpenAction?.Invoke(); }
    public void Inject(byte[] bytes) => _incoming.Add(bytes);
    public int Read(byte[] buffer)
    {
        ReadStarted.TrySetResult();
        if (ReadFailure is { } failure) throw failure;
        if (_pending is null)
        {
            if (!_incoming.TryTake(out _pending, 25)) throw new TimeoutException();
            _offset = 0;
        }
        int length = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsSpan(_offset, length).CopyTo(buffer);
        _offset += length;
        if (_offset == _pending.Length) _pending = null;
        return length;
    }
    public void Write(byte[] data)
    {
        WriteAction?.Invoke(data);
        Writes.Enqueue(data.ToArray());
        if (Echo) Inject(data.ToArray());
    }
    public void Dispose() { Interlocked.Increment(ref _disposeCount); Disposed.TrySetResult(); }
}
