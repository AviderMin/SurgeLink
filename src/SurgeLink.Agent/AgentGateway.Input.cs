using System.Security.Cryptography;
using System.Text.Json;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Agent;

public sealed partial class AgentGateway
{
    private static async Task<AgentResponse> ExecuteInputAsync(Grant grant, AgentRequest request, CancellationToken ct)
    {
        if (!grant.Info.TerminalControl || grant.Terminal is null)
            return AgentResponse.Error("control_required", "Allow terminal control in the desktop Agent panel first.");
        if (request.OperationId is not { Length: > 0 and <= 128 } operationId
            || !operationId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            return AgentResponse.Error("invalid_arguments", "An operationId of 1-128 ASCII letters, digits, dots, dashes or underscores is required.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, AgentJson.Options)));
        if (grant.Inputs.TryGetValue(operationId, out var prior))
            return prior.Fingerprint == fingerprint ? prior.Response
                : AgentResponse.Error("operation_conflict", "This operation ID was already used with different arguments.");
        if (grant.Inputs.Count >= 1024)
            return AgentResponse.Error("operation_limit", "Renew terminal control in the desktop for a new handle before more input.");
        if (request.ExpectedInputRevision is not >= 0)
            return AgentResponse.Error("invalid_arguments", "expectedInputRevision from read_terminal is required.");
        if (request.Method == "terminal.text" && (request.Text is null || request.Text.Length > 16384 || request.Text.Length == 0 && !request.Submit))
            return AgentResponse.Error("invalid_arguments", "Supply text up to 16384 characters, or submit an empty line explicitly.");
        if (request.Method == "terminal.key" && (string.IsNullOrWhiteSpace(request.Key) || request.Key.Length > 64))
            return AgentResponse.Error("invalid_arguments", "Supply a terminal key such as Enter, Tab, Ctrl+C or Up.");

        AgentResponse response;
        try
        {
            // Replaying the same operation never selects a tab or writes again.
            if (grant.Activate is not null) await grant.Activate("terminal", ct).ConfigureAwait(false);
            ThrowIfCancelled(grant, ct);
            var receipt = request.Method == "terminal.text"
                ? await grant.Terminal.SendTextAsync(request.Text!, request.Submit, request.ExpectedInputRevision.Value, ct).ConfigureAwait(false)
                : await grant.Terminal.SendKeyAsync(request.Key!, request.ExpectedInputRevision.Value, ct).ConfigureAwait(false);
            response = AgentResponse.Ok(new { operationId, receipt.InputRevision, receipt.OutputCursor, receipt.AcceptedBytes,
                receipt.Submitted, status = "input_sent", commandCompletionKnown = false });
        }
        catch (TerminalInputConflictException) { response = AgentResponse.Error("input_conflict", "Input changed. Read the terminal again before a new operation."); }
        catch (TerminalInputUnknownException) { response = AgentResponse.Error("input_outcome_unknown", "The PTY write may be partial. Observe the terminal; do not automatically resend with a new operation ID."); }
        catch (OperationCanceledException) { response = AgentResponse.Error("cancelled", "Input was cancelled before writing."); }
        catch (ArgumentException) { response = AgentResponse.Error("invalid_arguments", "Unsupported terminal key or input."); }
        catch (InvalidOperationException) { response = AgentResponse.Error("terminal_unavailable", "The terminal is closed or unavailable."); }
        catch (Exception) { response = AgentResponse.Error("input_outcome_unknown", "The input outcome is uncertain. Observe the terminal before continuing."); }
        grant.Inputs.Add(operationId, (fingerprint, response));
        // Do not replace a successful acknowledgement with cancellation after bytes were accepted.
        return response;
    }
}
