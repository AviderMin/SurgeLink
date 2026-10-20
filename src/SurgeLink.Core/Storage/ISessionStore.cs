using SurgeLink.Core.Sessions;

namespace SurgeLink.Core.Storage;

/// <summary>Persistence for saved session profiles.</summary>
public interface ISessionStore
{
    /// <summary>Loads every saved profile, newest first is not implied.</summary>
    IReadOnlyList<SessionProfile> Load();

    /// <summary>Replaces the whole profile set.</summary>
    void Save(IReadOnlyList<SessionProfile> profiles);

    /// <summary>Adds or replaces one profile.</summary>
    void Upsert(SessionProfile profile);

    /// <summary>Removes one profile by id.</summary>
    bool Delete(string profileId);
}
