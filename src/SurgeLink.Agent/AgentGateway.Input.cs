using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Agent;

public sealed partial class AgentGateway
{
    private async Task<AgentResponse> ExecuteInputAsync(Grant grant, AgentRequest request,
        System.Diagnostics.Stopwatch requestClock, CancellationToken ct)
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
        if (request.Method == "terminal.exec" && request.Text!.Contains('\0'))
            return AgentResponse.Error("invalid_arguments", "A POSIX shell command cannot contain NUL.");
        if (request.Method == "terminal.key" && (string.IsNullOrWhiteSpace(request.Key) || request.Key.Length > 64))
            return AgentResponse.Error("invalid_arguments", "Supply a terminal key such as Enter, Tab, Ctrl+C or Up.");
        var isText = request.Method is "terminal.text" or "terminal.exec";
        var marker = request.Method == "terminal.exec"
            ? "__SURGELINK_EXIT_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + "__"
            : null;
        var sendText = marker is null ? request.Text : BuildExecText(request.Text!, marker);

        // Consume a rate permit before consuming the one-time check token. A
        // throttled request must remain replayable without another Check Sessions.
        if (!grant.InputRate.TryAcquire())
            return AgentResponse.Error("input_rate_limited",
                "Too many terminal inputs in a short period. Wait, then retry; the check token was not consumed.");

        if (ValidateCheck(grant, request) is { } checkFailure) return checkFailure;
        var check = grant.Check!;
        grant.Check = null;

        AgentResponse response;
        var inputWritten = false;
        try
        {
            // Replaying the same operation never selects a tab or writes again.
            if (grant.Activate is not null) await grant.Activate("terminal", ct).ConfigureAwait(false);
            ThrowIfCancelled(grant, ct);
            if (_timeProvider.GetUtcNow() >= check.ExpiresUtc || !grant.Terminal.ReadScreen().MatchesContent(check.Screen)
                || grant.Activate is not null && grant.IsActive is not null && !grant.IsActive())
                return AgentResponse.Error("session_check_required", "The target changed or the check expired while selecting the terminal. Run Check Sessions again.");
            var receipt = isText
                ? await grant.Terminal.SendCheckedTextAsync(sendText!, request.Method == "terminal.exec" || request.Submit, check.Screen, ct).ConfigureAwait(false)
                : await grant.Terminal.SendCheckedKeyAsync(request.Key!, check.Screen, ct).ConfigureAwait(false);
            inputWritten = true;
            TouchControl(grant);
            if (marker is not null)
            {
                grant.LastInputText = request.Text;
                var (wait, exitCode) = await WaitOutputAsync(grant, request with { Cursor = receipt.OutputCursor }, ct,
                    marker + ":", RemainingWaitMilliseconds(requestClock)).ConfigureAwait(false);
                response = BuildExecResponse(operationId, receipt, wait, exitCode, marker, request.Text);
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
        catch (OperationCanceledException) when (!inputWritten) { response = AgentResponse.Error("cancelled", "Input was cancelled before writing."); }
        catch (OperationCanceledException) { response = AgentResponse.Error("input_outcome_unknown", "The command was sent to the terminal, but waiting for its completion was cancelled or timed out. Observe the terminal before continuing; do not resend with a new operation ID."); }
        catch (ArgumentException) { response = AgentResponse.Error("invalid_arguments", "Unsupported terminal key or input."); }
        catch (InvalidOperationException) { response = AgentResponse.Error("terminal_unavailable", "The terminal is closed or unavailable."); }
        catch (Exception) { response = AgentResponse.Error("input_outcome_unknown", "The input outcome is uncertain. Observe the terminal before continuing."); }
        if (response.ErrorCode == "input_outcome_unknown") grant.InputOutcomeUnknown = true;
        if (response.Success) grant.LastInputText = request.Method is "terminal.text" or "terminal.exec" ? request.Text : null;
        grant.Inputs.Remember(operationId, fingerprint, response);
        // Do not replace a successful acknowledgement with cancellation after bytes were accepted.
        return response;
    }

    private static string BuildExecText(string command, string marker)
    {
        // Parse the completion print before executing anything that can read stdin.
        var line = new StringBuilder("command eval \"$(printf '%b' ' ");
        foreach (var character in command.TrimEnd('\r', '\n').Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (character == '\'') { line.Append("'\\''"); }
            else if (character == '\\') { line.Append("\\\\"); }
            else if (char.IsAscii(character) && char.IsControl(character))
                line.Append("\\0").Append(Convert.ToString(character, 8).PadLeft(3, '0'));
            else { line.Append(character); }
        }
        return line.Append("')\"; printf '\\n").Append(marker).Append(":%d\\n' \"$?\"").ToString();
    }

    private static string CleanExecOutput(string text, string marker, string? echoText)
    {
        var command = echoText?.Trim();
        var echoRemoved = false;
        var filtered = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Contains(marker, StringComparison.Ordinal))
                continue;
            if (!echoRemoved && !string.IsNullOrEmpty(command)
                && string.Equals(line.Trim(), command, StringComparison.Ordinal))
            {
                echoRemoved = true;
                continue;
            }
            filtered.Add(line);
        }
        while (filtered.Count > 0 && string.IsNullOrEmpty(filtered[0])) filtered.RemoveAt(0);
        while (filtered.Count > 0 && string.IsNullOrEmpty(filtered[^1])) filtered.RemoveAt(filtered.Count - 1);
        return string.Join('\n', filtered);
    }

    private static AgentResponse BuildExecResponse(string operationId, TerminalInputReceipt receipt, AgentResponse wait,
        int? exitCode, string marker, string? echoText)
    {
        if (!wait.Success)
        {
            return wait with { Data = AgentResponse.Ok(new
            {
                operationId, receipt.InputRevision, receipt.OutputCursor, receipt.AcceptedBytes, receipt.Submitted,
                status = "input_sent", commandCompletionKnown = false, exitCode = (int?)null,
                waitError = wait.ErrorCode, output = wait.Data,
            }).Data };
        }

        var data = wait.Data!.Value;
        var text = CleanExecOutput(data.GetProperty("text").GetString() ?? string.Empty, marker, echoText);
        var timedOut = data.GetProperty("timedOut").GetBoolean();
        var status = exitCode is not null ? "executed" : timedOut ? "timed_out" : "input_sent";

        return AgentResponse.Ok(new
        {
            operationId, receipt.InputRevision, receipt.OutputCursor, receipt.AcceptedBytes, receipt.Submitted,
            status, commandCompletionKnown = exitCode is not null, timedOut, exitCode,
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
            screen = data.GetProperty("screen"),
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

        if (!current.MatchesContent(check.Screen))
            return AgentResponse.Error("session_check_required",
                "The visible terminal changed after the check (screen text, cursor or terminal modes). Scrolling alone does not invalidate a check. Read the current screen and run Check Sessions again.");

        if ((grant.IsActive?.Invoke() ?? false) != check.IsActive)
            return AgentResponse.Error("session_check_required",
                "The session's active state changed after the check. Select the intended terminal and run Check Sessions again.");

        return null;
    }
}
