using SurgeLink.Core.Sessions;

namespace SurgeLink.Core.Storage;

/// <summary>JSON backed profile store with atomic writes.</summary>
public sealed class JsonSessionStore : ISessionStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private List<SessionProfile> _profiles;

    /// <summary>Creates a store backed by <paramref name="path"/>.</summary>
    public JsonSessionStore(string path)
    {
        _path = path;
        _profiles = AtomicJsonFile.Read(path, new List<SessionProfile>())
            .Where(profile => profile is not null
                && !string.IsNullOrWhiteSpace(profile.Id)
                && !string.IsNullOrWhiteSpace(profile.ProtocolId)
                && profile.Name is not null && profile.Host is not null && profile.Username is not null)
            .Select(profile => profile.Options is null
                ? profile with { Options = new Dictionary<string, string>() } : profile)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionProfile> Load()
    {
        lock (_gate)
        {
            return _profiles.ToList();
        }
    }

    /// <inheritdoc />
    public void Save(IReadOnlyList<SessionProfile> profiles)
    {
        lock (_gate)
        {
            var next = profiles.ToList();
            AtomicJsonFile.Write(_path, next);
            _profiles = next;
        }
    }

    /// <inheritdoc />
    public void Upsert(SessionProfile profile)
    {
        lock (_gate)
        {
            var next = _profiles.ToList();
            var index = next.FindIndex(p => string.Equals(p.Id, profile.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                next[index] = profile;
            }
            else
            {
                next.Add(profile);
            }

            AtomicJsonFile.Write(_path, next);
            _profiles = next;
        }
    }

    /// <inheritdoc />
    public bool Delete(string profileId)
    {
        lock (_gate)
        {
            var next = _profiles.ToList();
            var removed = next.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                AtomicJsonFile.Write(_path, next);
                _profiles = next;
            }

            return removed;
        }
    }
}
