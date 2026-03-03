using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
using Lumi.Models;
using Microsoft.Extensions.AI;

namespace Lumi.Services;

public class CopilotService : IAsyncDisposable
{
    private CopilotClient? _client;
    private List<ModelInfo>? _models;
    private long _connectionGeneration;

    /// <summary>Fires after the CopilotClient has been replaced (reconnection).
    /// Consumers should discard any cached CopilotSession objects.</summary>
    public event Action? Reconnected;

    public bool IsConnected => _client?.State == ConnectionState.Connected;

    /// <summary>Monotonically increasing generation counter. Changes every time a
    /// new CopilotClient is created, allowing consumers to detect stale sessions.</summary>
    public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var oldClient = _client;

        _client = new CopilotClient(new CopilotClientOptions
        {
            LogLevel = "error",
            AutoRestart = true,
        });

        await _client.StartAsync(ct);
        Interlocked.Increment(ref _connectionGeneration);

        // Dispose the old client (stops the old CLI process) after the new one is ready.
        if (oldClient is not null)
        {
            Reconnected?.Invoke();
            try { await oldClient.DisposeAsync(); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Pings the CLI process with a timeout. Returns false if
    /// the client is missing, disconnected, or unresponsive.</summary>
    public async Task<bool> IsHealthyAsync(TimeSpan? timeout = null)
    {
        if (_client is null || _client.State != ConnectionState.Connected)
            return false;
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(8));
            await _client.PingAsync(cancellationToken: cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        _models ??= await _client.ListModelsAsync(ct);
        return _models;
    }

    /// <summary>Creates a new Copilot session with the given configuration.</summary>
    public async Task<CopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.CreateSessionAsync(config, ct);
    }

    /// <summary>Resumes an existing Copilot session by ID.</summary>
    public async Task<CopilotSession> ResumeSessionAsync(
        string sessionId, ResumeSessionConfig config, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.ResumeSessionAsync(sessionId, config, ct);
    }

    /// <summary>Retrieves the event log for a session (for replay/restore).</summary>
    public async Task<IReadOnlyList<SessionEvent>> GetSessionEventsAsync(
        CopilotSession session, CancellationToken ct = default)
    {
        return await session.GetMessagesAsync(ct);
    }

    /// <summary>Lists all known sessions, optionally filtered.</summary>
    public async Task<List<SessionMetadata>> ListSessionsAsync(
        SessionListFilter? filter = null, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.ListSessionsAsync(filter, ct);
    }

    public async Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.GetAuthStatusAsync(ct);
    }

    /// <summary>Lists built-in agents available in a session via the RPC Agent API.</summary>
    public async Task<List<GitHub.Copilot.SDK.Rpc.Agent>> ListSessionAgentsAsync(
        CopilotSession session, CancellationToken ct = default)
    {
        var result = await session.Rpc.Agent.ListAsync(ct);
        return result.Agents;
    }

    /// <summary>Selects a built-in agent by name for all future turns in the session.</summary>
    public async Task SelectSessionAgentAsync(
        CopilotSession session, string agentName, CancellationToken ct = default)
    {
        await session.Rpc.Agent.SelectAsync(agentName, ct);
    }

    /// <summary>Deselects the current built-in agent, returning the session to default routing.</summary>
    public async Task DeselectSessionAgentAsync(
        CopilotSession session, CancellationToken ct = default)
    {
        await session.Rpc.Agent.DeselectAsync(ct);
    }

    /// <summary>
    /// Launches the Copilot CLI login flow (OAuth device flow) and waits for completion.
    /// </summary>
    public async Task<bool> SignInAsync(CancellationToken ct = default)
    {
        var cliPath = FindCliPath();
        if (cliPath is null) return false;

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = "login",
            UseShellExecute = true, // Opens browser for OAuth
        };

        using var process = Process.Start(psi);
        if (process is null) return false;

        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0;
    }

    private static string? FindCliPath()
    {
        var binary = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";
        var appDir = AppContext.BaseDirectory;

        // Check runtimes/{rid}/native/ (standard SDK output location)
        var rid = RuntimeInformation.RuntimeIdentifier;
        var runtimePath = Path.Combine(appDir, "runtimes", rid, "native", binary);
        if (File.Exists(runtimePath)) return runtimePath;

        // Fallback: try portable rid
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var portablePath = Path.Combine(appDir, "runtimes", $"{os}-{arch}", "native", binary);
        if (File.Exists(portablePath)) return portablePath;

        // Fallback: check app directory directly
        var directPath = Path.Combine(appDir, binary);
        if (File.Exists(directPath)) return directPath;

        return null;
    }

    public async Task<string?> GenerateTitleAsync(string userMessage, CancellationToken ct = default)
    {
        if (_client is null) return null;

        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Content = "You are a title generator. Generate a concise title (3-6 words) for a chat conversation that started with the user message below. Respond with ONLY the title text, nothing else. No quotes, no punctuation at the end. Do not refuse or explain — just output the title.",
                Mode = SystemMessageMode.Replace
            },
            AvailableTools = [],
            OnPermissionRequest = PermissionHandler.ApproveAll,
        }, ct);

        try
        {
            var result = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = userMessage },
                TimeSpan.FromSeconds(30), ct);
            return result?.Data?.Content?.Trim();
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates a lightweight session with only the provided custom tools and a fully replaced
    /// system prompt. InfiniteSessions and skill directories are off. Built-in tools that could
    /// distract the model are excluded.
    /// </summary>
    public async Task<CopilotSession> CreateLightweightSessionAsync(
        string systemPrompt,
        string? model,
        List<AIFunction> tools,
        CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");

        var config = new SessionConfig
        {
            Model = model,
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Replace
            },
            Tools = tools,
            ExcludedTools =
            [
                "web_fetch", "web_search",
                "editFile", "readFile", "listDirectory", "createFile", "deleteFile",
                "runTerminalCommand", "getTerminalOutput",
                "searchFiles", "getFileInfo",
            ],
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        return await _client.CreateSessionAsync(config, ct);
    }

    public async Task<List<string>?> GenerateSuggestionsAsync(string assistantMessage, string? userMessage, CancellationToken ct = default)
    {
        if (_client is null) return null;

        var context = string.IsNullOrWhiteSpace(userMessage)
            ? assistantMessage
            : $"User: {userMessage}\n\nAssistant: {assistantMessage}";

        // Truncate to keep the request lightweight
        if (context.Length > 2000)
            context = context[..2000];

        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Content = "You generate follow-up suggestions for a chat assistant. Given the conversation below, produce exactly 3 short follow-up messages the user might want to send next. Each suggestion must be concise (under 60 characters) and contextually relevant. Output ONLY a JSON array of 3 strings, nothing else. Example: [\"Tell me more\", \"How do I implement this?\", \"What are the alternatives?\"]",
                Mode = SystemMessageMode.Replace
            },
            AvailableTools = [],
            OnPermissionRequest = PermissionHandler.ApproveAll,
        }, ct);

        try
        {
            var result = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = context },
                TimeSpan.FromSeconds(15), ct);
            var raw = result?.Data?.Content?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // Strip markdown code fences if present (e.g., ```json ... ```)
            if (raw.StartsWith("```"))
            {
                var firstNewline = raw.IndexOf('\n');
                if (firstNewline > 0) raw = raw[(firstNewline + 1)..];
                if (raw.EndsWith("```")) raw = raw[..^3];
                raw = raw.Trim();
            }

            // Parse the JSON array
            var suggestions = System.Text.Json.JsonSerializer.Deserialize(
                raw, Lumi.Models.AppDataJsonContext.Default.ListString);
            return suggestions?.Count > 0 ? suggestions : null;
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (_client is null) return;
        try { await _client.DeleteSessionAsync(sessionId, ct); }
        catch { /* Best-effort cleanup */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
