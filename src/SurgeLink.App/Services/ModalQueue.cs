namespace SurgeLink.App.Services;

/// <summary>UI-thread FIFO shared by every modal in one window.</summary>
internal sealed class ModalQueue
{
    private readonly List<Guid> _requests = new();
    public Guid? Current => _requests.Count == 0 ? null : _requests[0];
    public event Action? Changed;

    public Action Subscribe(Action listener)
    {
        Changed += listener;
        return () => Changed -= listener;
    }

    public void Enqueue(Guid id)
    {
        if (_requests.Contains(id)) return;
        _requests.Add(id);
        if (_requests.Count == 1) Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        var wasCurrent = Current == id;
        if (_requests.Remove(id) && wasCurrent) Changed?.Invoke();
    }
}
