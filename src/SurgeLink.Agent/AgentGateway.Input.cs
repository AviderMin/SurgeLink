using System.Security.Cryptography;
using System.Text.Json;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Agent;

public sealed partial class AgentGateway
{
    private async Task<AgentResponse> ExecuteInputAsync(Grant grant, AgentRequest request, CancellationToken ct)
    {
        if (!grant.Info.TerminalControl || grant.Terminal is null)
            return AgentResponse.Error("control_required", "Allow terminal control in the desktop Agent panel first.");
        if (request.OperationId is not { Length: > 0 and <= 128 } operationId
            || !operationId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            return AgentResponse.Error("invalid_arguments", "An operationId of 1-128 ASCII letters, digits, dots, dashes or underscores is required.");
        // Only arguments that affect this operation participate in replay identity.
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.Method, request.ExpectedInputRevision, request.CheckToken,
            text = request.Method is "terminal.text" or "terminal.exec" ? request.Text : null,
            submit = request.Method == "terminal.exec" || request.Method == "terminal.text" && request.Submit,
            key = request.Method == "terminal.key" ? request.Key : null,
        }, AgentJson.Options)));
        if (grant.Inputs.TryGetValue(operationId, out var prior))
            return prior.Fingerprint == fingerprint ? prior.Response
                : AgentResponse.Error("operation_conflict", "This operation ID was already used with different arguments.");
        if (grant.InputOutcomeUnknown)
            return AgentResponse.Error("input_outcome_unknown", "A previous PTY write may be partial. Inspect the terminal and renew terminal control in the desktop before sending more input.");
        if (request.ExpectedInputRevision is not >= 0)
            return AgentResponse.Error("invalid_arguments", "expectedInputRevision from read_terminal is required.");
        if ((request.Method is "terminal.text" or "terminal.exec") && (request.Text is null || request.Text.Length > 16384 || request.Text.Length == 0 && !request.Submit))
            return AgentResponse.Error("invalid_arguments", "Supply text up to 16384 characters, or submit an empty line explicitly.");
        if (request.Method == "terminal.key" && (string.IsNullOrWhiteSpace(request.Key) || request.Key.Length > 64))
            return AgentResponse.Error("invalid_arguments", "Supply a terminal key such as Enter, Tab, Ctrl+C or Up.");
        var isText = request.Method is "terminal.text" or "terminal.exec";
        var sendText = isText ? request.Method == "terminal.exec" ? BuildExecText(request.Text!, operationId) : request.Text : null;

        if (ValidateCheck(grant, request) is { } checkFailure) return checkFailure;
        var check = grant.Check!;
        grant.Check = null;

        AgentResponse response;
        try
        {
            // Replaying the same operation never selects a tab or writes again.
            if (grant.Activate is not null) await grant.Activate("terminal", ct).ConfigureAwait(false);
            ThrowIfCancelled(grant, ct);
            if (_timeProvider.GetUtcNow() >= check.ExpiresUtc || grant.Terminal.ReadScreen() != check.Screen
                || grant.Activate is not null && grant.IsActive is not null && !grant.IsActive())
                return AgentResponse.Error("session_check_required", "The target changed or the check expired while selecting the terminal. Run Check Sessions again.");
            var receipt = isText
                ? await grant.Terminal.SendCheckedTextAsync(sendText!, request.Method == "terminal.exec" || request.Submit, check.Screen, ct).ConfigureAwait(false)
                : await grant.Terminal.SendCheckedKeyAsync(request.Key!, check.Screen, ct).ConfigureAwait(false);
            if (request.Method == "terminal.exec")
            {
                grant.LastInputText = request.Text;
                var marker = ExecMarker(operationId);
                var wait = await WaitOutputAsync(grant, request with { Cursor = receipt.OutputCursor }, ct, marker + ":").ConfigureAwait(false);
                response = BuildExecResponse(operationId, receipt, wait, marker, request.Text);
            }
            else
            {
                response = AgentResponse.Ok(new { operationId, receipt.InputRevision, receipt.OutputCursor, receipt.AcceptedBytes,
                    receipt.Submitted, status = "input_sent", commandCompletionKnown = false });
            }
        }
        catch (AgentOperationPausedException) { response = HumanOperatingError(); }
        catch (TerminalInputConflictException) { response = AgentResponse.Error("input_conflict", "Input changed. Read the terminal again before a new operation."); }
        catch (TerminalInputUnknownException) { response = AgentResponse.Error("input_outcome_unknown", "The PTY write may be partial. Observe the terminal; do not automatically resend with a new operation ID."); }
        catch (OperationCanceledException) { response = AgentResponse.Error("cancelled", "Input was cancelled before writing."); }
        catch (ArgumentException) { response = AgentResponse.Error("invalid_arguments", "Unsupported terminal key or input."); }
        catch (InvalidOperationException) { response = AgentResponse.Error("terminal_unavailable", "The terminal is closed or unavailable."); }
        catch (Exception) { response = AgentResponse.Error("input_outcome_unknown", "The input outcome is uncertain. Observe the terminal before continuing."); }
        if (response.ErrorCode == "input_outcome_unknown") grant.InputOutcomeUnknown = true;
        if (response.Success) grant.LastInputText = request.Method is "terminal.text" or "terminal.exec" ? request.Text : null;
        RememberInput(grant, operationId, fingerprint, response);
        // Do not replace a successful acknowledgement with cancellation after bytes were accepted.
        return response;
    }

    private static string ExecMarker(string operationId) => "__SURGELINK_EXIT_" + operationId + "__";

    private static string BuildExecText(string command, string operationId) =>
        command + "\n__surge_status=$?; printf '\\n" + ExecMarker(operationId) + ":%d\\n' \"$__surge_status\"";

    private static string CleanExecOutput(string text, string marker, string? echoText)
    {
        var command = echoText?.Trim();
        return string.Join('\n', text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Where(line => !line.Contains("__surge_status=$?; printf", StringComparison.Ordinal)
                               && !line.Contains(marker, StringComparison.Ordinal)
                               && (string.IsNullOrEmpty(command)
                                   || !string.Equals(line.Trim(), command, StringComparison.Ordinal))))
            .Trim();
    }

    private static AgentResponse BuildExecResponse(string operationId, TerminalInputReceipt receipt, AgentResponse wait, string marker, string? echoText)
    {
        if (!wait.Success)
        {
            return AgentResponse.Ok(new
            {
                operationId, receipt.InputRevision, receipt.OutputCursor, receipt.AcceptedBytes, receipt.Submitted,
                status = "executed", commandCompletionKnown = false, exitCode = (int?)null,
                waitError = wait.ErrorCode, output = wait.Data,
            });
        }

        var data = wait.Data!.Value;
        var text = data.GetProperty("text").GetString() ?? string.Empty;
        var prefix = marker + ":";
        int? exitCode = null;
        // The shell echoes the sentinel command itself, including the "%d"
        // placeholder. The real completion marker is the last one.
        var index = text.LastIndexOf(prefix, StringComparison.Ordinal);
        if (index >= 0)
        {
            var end = text.IndexOf('\n', index);
            if (end < 0) end = text.Length;
            var value = text[(index + prefix.Length)..end].Trim();
            if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                exitCode = parsed;
            text = text.Remove(index, end - index).TrimEnd();
        }
        text = CleanExecOutput(text, marker, echoText);

        return AgentResponse.Ok(new
        {
            operationId, receipt.InputRevision, receipt.OutputCursor, receipt.AcceptedBytes, receipt.Submitted,
            status = "executed", commandCompletionKnown = exitCode is not null, exitCode,
            output = new
            {
                text,
                cursor = data.GetProperty("cursor").GetInt64(),
                oldestCursor = data.GetProperty("oldestCursor").GetInt64(),
                latestCursor = data.GetProperty("latestCursor").GetInt64(),
                gap = data.GetProperty("gap").GetBoolean(),
                hasMore = data.GetProperty("hasMore").GetBoolean(),
                truncated = data.GetProperty("truncated").GetBoolean(),
            },
        });
    }

    /// <summary>
    /// Reports exactly which property invalidated a check. The error code stays stable
    /// for clients, while the message tells an Agent whether to wait, re-check or ask
    /// the user to resume control.
    /// </summary>
    private AgentResponse? ValidateCheck(Grant grant, AgentRequest request)
    {
        if (grant.Check is not { } check || request.CheckToken != check.Token)
            return AgentResponse.Error("session_check_required",
                "No check token matches this session. Run Check Sessions and supply its checkToken before this input.");

        if (_timeProvider.GetUtcNow() >= check.ExpiresUtc)
            return AgentResponse.Error("session_check_required",
                "The check token expired. Run Check Sessions again; the target may have changed since the previous check.");

        var current = grant.Terminal!.ReadScreen();
        if (request.ExpectedInputRevision != check.Screen.InputRevision
            || current.InputRevision != check.Screen.InputRevision)
            return AgentResponse.Error("session_check_required",
                "The terminal input revision changed after the check. Read the current screen and run Check Sessions again.");

        if (current != check.Screen)
            return AgentResponse.Error("session_check_required",
                "The visible terminal changed after the check (including background output). Read the current screen and run Check Sessions again.");

        if ((grant.IsActive?.Invoke() ?? false) != check.IsActive)
            return AgentResponse.Error("session_check_required",
                "The session's active state changed after the check. Select the intended terminal and run Check Sessions again.");

        return null;
    }

    private static void RememberInput(Grant grant, string operationId, string fingerprint, AgentResponse response)
    {
        grant.Inputs[operationId] = (fingerprint, response);
        grant.InputOrder.Enqueue(operationId);
        // Keep exact-retry protection for the recent history without forcing the user
        // to re-authorize a long-running terminal after 1024 commands.
        while (grant.Inputs.Count > 1024 && grant.InputOrder.TryDequeue(out var oldest))
        {
            if (oldest != operationId) grant.Inputs.Remove(oldest);
        }
    }
}
