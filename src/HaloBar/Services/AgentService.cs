using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaloBar.Services;

internal enum AgentState { Working, Waiting, Done, Error }

internal sealed class AgentSession
{
    public required string Id { get; init; }
    public required string Agent { get; init; }
    public string Project { get; set; } = "";
    public AgentState State { get; set; } = AgentState.Working;
    public string Doing { get; set; } = "";
    public DateTime Since { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
}

/// <summary>
/// Follows AI coding agents (Claude Code, and anything else through the local API) and shows
/// what each is doing: working, waiting for you, done. Claude Code reports through hooks that
/// HaloBar adds to ~/.claude/settings.json when you press Connect; they post each event to
/// 127.0.0.1, so nothing about your sessions leaves the PC.
/// </summary>
internal sealed class AgentService
{
    private const string Marker = "halobar-agent-hook";
    private readonly Dictionary<string, AgentSession> _sessions = [];
    private readonly object _gate = new();
    private readonly int _port;

    public AgentService(int port) => _port = port;

    public IReadOnlyList<AgentSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                // Finished sessions fade out after a while.
                foreach (var stale in _sessions.Values.Where(s => s.State is AgentState.Done or AgentState.Error && DateTime.Now - s.Updated > TimeSpan.FromMinutes(20)).ToList())
                    _sessions.Remove(stale.Id);
                return [.. _sessions.Values.OrderByDescending(s => s.Updated)];
            }
        }
    }

    /// <summary>(session, previous state): raised when a session changes state.</summary>
    public event Action<AgentSession, AgentState?>? StateChanged;
    public event Action? Changed;

    // ───────────────────────────── events in ─────────────────────────────

    /// <summary>A Claude Code hook event: the JSON Claude Code passes to every hook.</summary>
    public void OnClaudeEvent(string json)
    {
        JsonNode? e;
        try { e = JsonNode.Parse(json); }
        catch (JsonException) { return; }
        if (e == null) return;
        var id = e["session_id"]?.GetValue<string>() ?? "claude";
        var name = e["hook_event_name"]?.GetValue<string>() ?? "";
        var cwd = e["cwd"]?.GetValue<string>() ?? "";
        var (state, doing) = name switch
        {
            "UserPromptSubmit" => (AgentState.Working, "Thinking…"),
            "PreToolUse" => (AgentState.Working, Describe(e["tool_name"]?.GetValue<string>(), e["tool_input"])),
            "PostToolUse" => (AgentState.Working, "Thinking…"),
            "Notification" => (AgentState.Waiting, e["message"]?.GetValue<string>() ?? "Needs you"),
            "Stop" => (AgentState.Done, "Finished"),
            "SessionEnd" => (AgentState.Done, "Session ended"),
            "SessionStart" => (AgentState.Done, "Ready"),
            _ => ((AgentState?)null, ""),
        };
        if (state == null) return;
        Update(id, "Claude", Path.GetFileName(cwd.TrimEnd('\\', '/')), state.Value, doing);
    }

    /// <summary>A custom agent through the local API: /agent?name=..&amp;state=working|waiting|done|error&amp;text=..</summary>
    public void OnCustom(string name, string? stateText, string? text, string? project)
    {
        var state = stateText?.ToLowerInvariant() switch
        {
            "waiting" or "input" => AgentState.Waiting,
            "done" or "finished" or "idle" => AgentState.Done,
            "error" or "failed" => AgentState.Error,
            _ => AgentState.Working,
        };
        Update($"custom:{name}:{project}", name, project ?? "", state, text ?? state.ToString());
    }

    private void Update(string id, string agent, string project, AgentState state, string doing)
    {
        AgentSession session;
        AgentState? previous;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out session!))
            {
                session = new AgentSession { Id = id, Agent = agent, Project = project };
                _sessions[id] = session;
                previous = null;
            }
            else previous = session.State;
            if (previous != state) session.Since = DateTime.Now;
            session.State = state;
            session.Doing = doing;
            session.Updated = DateTime.Now;
            if (project.Length > 0) session.Project = project;
        }
        if (previous != state) StateChanged?.Invoke(session, previous);
        Changed?.Invoke();
    }

    private static string Describe(string? tool, JsonNode? input)
    {
        string? File(string key) => input?[key]?.GetValue<string>() is { } p ? Path.GetFileName(p) : null;
        return tool switch
        {
            "Edit" or "MultiEdit" => $"Editing {File("file_path")}",
            "Write" => $"Writing {File("file_path")}",
            "Read" => $"Reading {File("file_path")}",
            "Bash" or "PowerShell" => input?["description"]?.GetValue<string>() ?? "Running a command",
            "Grep" or "Glob" => "Searching",
            "WebFetch" or "WebSearch" => "Looking on the web",
            "Task" or "Agent" => "Working with a helper",
            null => "Working",
            _ => tool,
        };
    }

    // ───────────────────────────── Claude Code connection ─────────────────────────────

    private static string ClaudeSettings => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    private static readonly string[] ClaudeEvents = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"];

    public bool ClaudeInstalled => Directory.Exists(Path.GetDirectoryName(ClaudeSettings));

    public bool ClaudeConnected
    {
        get
        {
            try { return File.Exists(ClaudeSettings) && File.ReadAllText(ClaudeSettings).Contains(Marker); }
            catch (IOException) { return false; }
        }
    }

    /// <summary>
    /// Adds HaloBar's hooks to Claude Code's user settings, after copying the file aside. Each hook
    /// pipes Claude Code's event JSON to the local API with curl and never blocks Claude Code.
    /// </summary>
    public string ConnectClaude()
    {
        try
        {
            var root = LoadClaudeSettings();
            if (File.Exists(ClaudeSettings)) File.Copy(ClaudeSettings, ClaudeSettings + ".halobar-backup", overwrite: true);
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            root["hooks"] = hooks;
            var command = $"curl -s -m 2 -X POST -H \"Content-Type: application/json\" --data-binary @- \"http://127.0.0.1:{_port}/agent/claude?{Marker}\" >/dev/null 2>&1 || true";
            foreach (var name in ClaudeEvents)
            {
                var list = hooks[name] as JsonArray ?? new JsonArray();
                hooks[name] = list;
                RemoveOurs(list);
                list.Add(new JsonObject
                {
                    ["matcher"] = "",
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = 5 }),
                });
            }
            Save(root);
            Log.Write("Agents: Claude Code hooks added");
            return "Connected. New Claude Code sessions report here.";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Write($"Agents: could not connect Claude Code: {ex.Message}");
            return "Couldn't edit Claude Code's settings";
        }
    }

    /// <summary>Removes exactly the hooks <see cref="ConnectClaude"/> added.</summary>
    public string DisconnectClaude()
    {
        try
        {
            var root = LoadClaudeSettings();
            if (root["hooks"] is JsonObject hooks)
            {
                foreach (var name in hooks.Select(h => h.Key).ToList())
                {
                    if (hooks[name] is not JsonArray list) continue;
                    RemoveOurs(list);
                    if (list.Count == 0) hooks.Remove(name);
                }
                if (hooks.Count == 0) root.Remove("hooks");
            }
            Save(root);
            Log.Write("Agents: Claude Code hooks removed");
            return "Disconnected";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Write($"Agents: could not disconnect Claude Code: {ex.Message}");
            return "Couldn't edit Claude Code's settings";
        }
    }

    private static JsonObject LoadClaudeSettings()
    {
        if (!File.Exists(ClaudeSettings)) return new JsonObject();
        var text = File.ReadAllText(ClaudeSettings);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject()
            : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidOperationException("settings.json isn't an object");
    }

    private static void RemoveOurs(JsonArray list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
            if (list[i]?.ToJsonString().Contains(Marker) == true) list.RemoveAt(i);
    }

    private static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ClaudeSettings)!);
        File.WriteAllText(ClaudeSettings, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
