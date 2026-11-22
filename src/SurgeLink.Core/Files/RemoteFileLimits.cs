namespace SurgeLink.Core.Files;

/// <summary>
/// One nesting bound shared by recursive remote operations. A copy must stay inside the
/// depth that the same connection can delete, otherwise a failed copy leaves a staging
/// tree that its own rollback is not allowed to remove.
/// </summary>
public static class RemoteFileLimits
{
    public const int MaximumDepth = 128;
}