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
        _profiles = AtomicJsonFile.Read(path, new List<SessionProfile>());
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
            _profiles = profiles.ToList();
            AtomicJsonFile.Write(_path, _profiles);
        }
    }

    /// <inheritdoc />
    public void Upsert(SessionProfile profile)
    {
        lock (_gate)
        {
            var index = _profiles.FindIndex(p => string.Equals(p.Id, profile.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _profiles[index] = profile;
            }
            else
            {
                _profiles.Add(profile);
            }

            AtomicJsonFile.Write(_path, _profiles);
        }
    }

    /// <inheritdoc />
    public bool Delete(string profileId)
    {
        lock (_gate)
        {
            var removed = _profiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                AtomicJsonFile.Write(_path, _profiles);
            }

            return removed;
        }
    }
}
