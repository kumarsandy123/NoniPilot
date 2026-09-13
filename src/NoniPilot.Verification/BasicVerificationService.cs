using System.Diagnostics;
using System.Text.Json;
using NoniPilot.Domain.Interfaces;
using NoniPilot.Domain.Models;

namespace NoniPilot.Verification;

/// <summary>
/// Phase-1 IVerificationService: checks file/process-level post-conditions only. Screen-state
/// verification (UI Automation tree comparison, OCR, vision) is explicitly Phase 2 (section 15) -
/// this class must never claim a confidence that implies it looked at the screen.
/// </summary>
public sealed class BasicVerificationService : IVerificationService
{
    public Task<VerificationResult> VerifyAsync(TaskStep step, CancellationToken cancellationToken = default)
    {
        JsonElement parameters;
        try
        {
            parameters = JsonDocument.Parse(step.ParametersJson).RootElement;
        }
        catch (JsonException)
        {
            parameters = default;
        }

        var key = $"{step.Tool}.{step.Action}";

        return Task.FromResult(key switch
        {
            "FileSystem.CreateDirectory" => VerifyPathExists(parameters, "path", shouldExist: true),
            "FileSystem.Copy" => VerifyPathExists(parameters, "destinationPath", shouldExist: true),
            "FileSystem.Move" or "FileSystem.Rename" => VerifyPathExists(parameters, "destinationPath", shouldExist: true),
            "FileSystem.Delete" => VerifyPathExists(parameters, "path", shouldExist: false),
            "Application.Launch" => VerifyProcessRunning(step.ResultJson),
            "Application.Close" => VerifyProcessClosed(parameters, step.ResultJson),
            _ => new VerificationResult(
                Verified: true,
                Reason: $"No dedicated verifier for '{key}' yet - treated as successful based on execution completing without error. Screen-state verification arrives in Phase 2.",
                Confidence: 0.5),
        });
    }

    private static VerificationResult VerifyPathExists(JsonElement parameters, string propertyName, bool shouldExist)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty(propertyName, out var pathProp))
        {
            return new VerificationResult(false, $"Step parameters had no '{propertyName}' to verify against.", 0.0);
        }

        var path = pathProp.GetString();
        if (string.IsNullOrWhiteSpace(path))
        {
            return new VerificationResult(false, $"'{propertyName}' was empty.", 0.0);
        }

        var exists = Directory.Exists(path) || File.Exists(path);
        var verified = exists == shouldExist;

        return new VerificationResult(
            verified,
            verified
                ? $"'{path}' {(shouldExist ? "exists" : "no longer exists")} as expected."
                : $"'{path}' {(exists ? "still exists" : "does not exist")}, which does not match the expected post-condition.",
            Confidence: 1.0);
    }

    /// <summary>
    /// Measured live (2026-09-13): before this verifier existed, Application.Close fell through
    /// to the generic "no dedicated verifier" case above, which always reports Verified: true -
    /// so the model told the user "Google Chrome has been closed" while the audit database and a
    /// live process check both showed it was still running. WindowsApplicationService.CloseAsync
    /// was ALSO fixed the same day to stop unconditionally returning true, but that fix alone
    /// isn't enough - this verifier is what actually makes a false claim visible as a failed
    /// step instead of being reported as an unconditional success regardless of the tool's own
    /// result.
    /// </summary>
    private static VerificationResult VerifyProcessClosed(JsonElement parameters, string? resultJson)
    {
        var reportedClosed = false;
        if (!string.IsNullOrWhiteSpace(resultJson))
        {
            try
            {
                var result = JsonDocument.Parse(resultJson).RootElement;
                reportedClosed = result.TryGetProperty("closed", out var closedProp) && closedProp.GetBoolean();
            }
            catch (JsonException)
            {
                // Malformed result - treated as "not confirmed closed" below.
            }
        }

        if (!reportedClosed)
        {
            return new VerificationResult(
                false,
                "The close call itself reported failure (no window to close, or the application didn't exit in time).",
                Confidence: 1.0);
        }

        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("processId", out var pidProp))
        {
            return new VerificationResult(true, "Close reported successful; no processId parameter available to double-check against.", 0.6);
        }

        var processId = pidProp.GetInt32();
        try
        {
            using var process = Process.GetProcessById(processId);
            var stillRunning = !process.HasExited;
            return new VerificationResult(
                !stillRunning,
                stillRunning ? $"Process {processId} is still running despite being reported closed." : $"Process {processId} has exited.",
                Confidence: 1.0);
        }
        catch (ArgumentException)
        {
            // GetProcessById throws when no such process exists - i.e. it's genuinely gone.
            return new VerificationResult(true, $"Process {processId} no longer exists.", 1.0);
        }
    }

    private static VerificationResult VerifyProcessRunning(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return new VerificationResult(false, "No process id recorded for this launch step.", 0.0);
        }

        try
        {
            var result = JsonDocument.Parse(resultJson).RootElement;
            var processId = result.GetProperty("processId").GetInt32();
            using var process = Process.GetProcessById(processId);
            var running = !process.HasExited;
            return new VerificationResult(running, running ? $"Process {processId} is running." : $"Process {processId} has already exited.", 1.0);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return new VerificationResult(false, $"Could not verify launched process: {ex.Message}", 0.0);
        }
    }
}
