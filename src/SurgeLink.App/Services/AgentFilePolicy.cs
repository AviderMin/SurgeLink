using SurgeLink.Agent;
using SurgeLink.App.Localization;
using SurgeLink.Core.Security;

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

        var localPath = DiagnosticText.Escape(string.IsNullOrWhiteSpace(access.LocalPath) ? "(missing)" : access.LocalPath);
        var remotePath = DiagnosticText.Escape(access.Path ?? string.Empty);
        var label = DiagnosticText.Escape(access.Label);
        var session = access.Target is { } target
            ? $"{label} ({DiagnosticText.Escape(target.Username)}@{DiagnosticText.Escape(target.Server)})"
            : label;
        var overwrite = access.Overwrite
            ? access.Operation == "files.download"
                ? Str.T("shell.agent.numberedDownload", "If the name is taken, save a numbered copy in the same folder.")
                : Str.T("shell.agent.overwriteAllowed", "Overwrite permitted")
            : Str.T("shell.agent.overwriteForbidden", "Overwrite disabled");
        var title = access.Operation == "files.upload"
            ? Str.T("shell.agent.localUploadTitle", "Allow the Agent to read a local file?")
            : Str.T("shell.agent.localDownloadTitle", "Allow the Agent to write a local file?");
        var message = access.Operation == "files.upload"
            ? Str.F(
                "shell.agent.localUploadBody",
                "The Agent wants to upload this file from your computer:\n\n{0}\n\nSession: {2}\nRemote destination: {1}\n{3}\n\nAllow this once?",
                localPath,
                remotePath,
                session,
                overwrite)
            : Str.F(
                "shell.agent.localDownloadBody",
                "The Agent wants to save remote content to this file on your computer:\n\n{0}\n\nSession: {2}\nRemote source: {1}\n{3}\n\nAllow this once?",
                localPath,
                remotePath,
                session,
                overwrite);
        return await interaction.RequestConfirmationAsync(
            new ConfirmationRequest(
                title,
                message,
                Str.T("shell.agent.localFileAllow", "Allow once"),
                Str.T("common.cancel", "Cancel")),
            cancellationToken).ConfigureAwait(false);
    }
}
