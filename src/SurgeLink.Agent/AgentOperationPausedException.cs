namespace SurgeLink.Agent;

/// <summary>A deferred host action was blocked by human takeover before execution.</summary>
public sealed class AgentOperationPausedException() : InvalidOperationException("The user is operating this session.");
