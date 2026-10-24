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
        _profiles = ReadProfiles();
    }

    private List<SessionProfile> ReadProfiles(bool tolerateErrors = true) => AtomicJsonFile.Read(_path, new List<SessionProfile>(), tolerateErrors)
            .Where(profile => profile is not null
                && !string.IsNullOrWhiteSpace(profile.Id)
                && !string.IsNullOrWhiteSpace(profile.ProtocolId)
                && profile.Name is not null && profile.Host is not null && profile.Username is not null)
            .Select(profile => profile.Options is null
                ? profile with { Options = new Dictionary<string, string>() } : profile)
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<SessionProfile> Load()
    {
        lock (_gate)
        {
            _profiles = ReadProfiles();
            return _profiles.ToList();
        }
    }

    /// <inheritdoc />
    public void Upsert(SessionProfile profile)
    {
        lock (_gate)
        {
            AtomicJsonFile.WithExclusiveAccess(_path, () =>
            {
                var next = ReadProfiles(tolerateErrors: false);
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
                return true;
            });
        }
    }

    /// <inheritdoc />
    public bool Delete(string profileId)
    {
        lock (_gate)
        {
            return AtomicJsonFile.WithExclusiveAccess(_path, () =>
            {
                var next = ReadProfiles(tolerateErrors: false);
                var removed = next.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.Ordinal)) > 0;
                if (removed)
                {
                    AtomicJsonFile.Write(_path, next);
                    _profiles = next;
                }

                return removed;
            });
        }
    }
}
