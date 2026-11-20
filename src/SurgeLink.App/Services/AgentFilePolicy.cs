using SurgeLink.Agent;
using SurgeLink.App.Localization;

namespace SurgeLink.App.Services;

/// <summary>
/// Host policy for Agent file operations. Remote reads and writes stay covered by
/// the session grant the user already gave. Local upload/download touches files
/// on this machine, outside the shared session, so every such operation requires
/// an explicit confirmation that includes the exact local path.
/// </summary>
internal static class AgentFilePolicy
{
    public static async Task<bool> ConfirmAsync(
        InteractionService? interaction,
        AgentFileAccess access,
        CancellationToken cancellationToken)
    {
        if (access.Operation is not ("files.upload" or "files.download"))
        {
            return true;
        }

        if (interaction is null)
        {
            // No UI available (headless composition): fail closed.
            return false;
        }

        var localPath = string.IsNullOrWhiteSpace(access.LocalPath) ? "(missing)" : access.LocalPath;
        var title = access.Operation == "files.upload"
            ? Str.T("shell.agent.localUploadTitle", "Allow the Agent to read a local file?")
            : Str.T("shell.agent.localDownloadTitle", "Allow the Agent to write a local file?");
        var message = access.Operation == "files.upload"
            ? Str.F(
                "shell.agent.localUploadBody",
                "The Agent wants to upload this file from your computer:\n\n{0}\n\nRemote destination: {1}\n\nAllow this once?",
                localPath,
                access.Path)
            : Str.F(
                "shell.agent.localDownloadBody",
                "The Agent wants to save remote content to this file on your computer:\n\n{0}\n\nRemote source: {1}\n\nAllow this once?",
                localPath,
                access.Path);
        return await interaction.RequestConfirmationAsync(
            new ConfirmationRequest(
                title,
                message,
                Str.T("shell.agent.localFileAllow", "Allow once"),
                Str.T("common.cancel", "Cancel")),
            cancellationToken).ConfigureAwait(false);
    }
}
