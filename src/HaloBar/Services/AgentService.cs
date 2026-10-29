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
/// An agent HaloBar can connect: where its hooks live, which events to ask for, and the hook
/// timeout in the agent's own unit (Gemini CLI counts milliseconds, the others seconds).
/// <paramref name="WindowsCommand"/> marks agents that take a separate Windows command.
/// </summary>
internal sealed record HookTarget(string Id, string Name, string SettingsPath, string[] Events, int Timeout, bool WindowsCommand, string Note);

/// <summary>
/// Follows AI coding agents (Claude Code, Codex, Gemini CLI, and anything else through the local
/// API) and shows what each is doing: working, waiting for you, done. The three report through
/// hooks HaloBar adds to their user settings when you press Connect; the hooks post each event
/// to 127.0.0.1, so nothing about your sessions leaves the PC.
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
                // Finished sessions fade out after a while; one silent for two hours (an agent that
                // crashed or was closed mid-task) goes too.
                var now = DateTime.Now;
                foreach (var stale in _sessions.Values.Where(s => now - s.Updated > (s.State is AgentState.Done or AgentState.Error
                             ? TimeSpan.FromMinutes(20) : TimeSpan.FromHours(2))).ToList())
                    _sessions.Remove(stale.Id);
                return [.. _sessions.Values.OrderByDescending(s => s.Updated)];
            }
        }
    }

    /// <summary>(session, previous state): raised when a session changes state.</summary>
    public event Action<AgentSession, AgentState?>? StateChanged;
    public event Action? Changed;

    // ───────────────────────────── events in ─────────────────────────────

    /// <summary>
    /// A hook event from Claude Code, Codex or Gemini CLI: the JSON each passes to its hooks.
    /// Codex uses Claude Code's event names; Gemini CLI has its own.
    /// </summary>
    public void OnHookEvent(string agent, string json)
    {
        var target = Targets.FirstOrDefault(t => t.Id == agent);
        if (target == null) return;
        JsonNode? e;
        try { e = JsonNode.Parse(json); }
        catch (JsonException) { return; }
        if (e is not JsonObject) return;
        var (state, doing) = (Text(e, "hook_event_name") ?? "") switch
        {
            // Claude Code and Codex
            "UserPromptSubmit" => (AgentState.Working, "Thinking…"),
            "PreToolUse" => (AgentState.Working, Describe(Text(e, "tool_name"), e["tool_input"])),
            "PostToolUse" or "SubagentStop" or "PreCompact" or "PostCompact" => (AgentState.Working, "Thinking…"),
            "SubagentStart" => (AgentState.Working, "Working with a helper"),
            "PermissionRequest" => (AgentState.Waiting, "Wants to: " + Describe(Text(e, "tool_name"), e["tool_input"])),
            "Notification" => (AgentState.Waiting, Text(e, "message") ?? "Needs you"),
            "Stop" => (AgentState.Done, "Finished"),
            "Interrupt" => (AgentState.Done, "Stopped"),
            // Gemini CLI
            "BeforeAgent" => (AgentState.Working, "Thinking…"),
            "BeforeTool" => (AgentState.Working, Describe(Text(e, "tool_name"), e["tool_input"])),
            "AfterTool" or "PreCompress" => (AgentState.Working, "Thinking…"),
            "AfterAgent" => (AgentState.Done, "Finished"),
            // All of them
            "SessionStart" => (AgentState.Done, "Ready"),
            "SessionEnd" => (AgentState.Done, "Session ended"),
            _ => ((AgentState?)null, ""),
        };
        if (state == null) return;
        var cwd = Text(e, "cwd") ?? "";
        Update($"{agent}:{Text(e, "session_id") ?? "session"}", target.Name, Path.GetFileName(cwd.TrimEnd('\\', '/')), state.Value, doing);
    }

    /// <summary>A Claude Code hook event, as posted to /agent/claude.</summary>
    public void OnClaudeEvent(string json) => OnHookEvent("claude", json);

    private static string? Text(JsonNode e, string key) => e[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

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

    /// <summary>A tool call in a few words; tool names are Claude Code's, Codex's and Gemini CLI's.</summary>
    private static string Describe(string? tool, JsonNode? input)
    {
        string? Arg(string key) => input is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        string? File(params string[] keys) => keys.Select(Arg).FirstOrDefault(p => p != null) is { } p ? Path.GetFileName(p) : null;
        return tool switch
        {
            "Edit" or "MultiEdit" or "replace" => $"Editing {File("file_path")}",
            "Write" or "write_file" => $"Writing {File("file_path")}",
            "Read" or "read_file" => $"Reading {File("file_path", "absolute_path")}",
            "read_many_files" => "Reading files",
            "apply_patch" => "Editing files",
            "Bash" or "PowerShell" or "run_shell_command" or "shell" => Arg("description") ?? "Running a command",
            "Grep" or "Glob" or "glob" or "grep" or "search_file_content" or "list_directory" => "Searching",
            "WebFetch" or "WebSearch" or "web_fetch" or "google_web_search" => "Looking on the web",
            "Task" or "Agent" => "Working with a helper",
            null => "Working",
            _ => tool,
        };
    }

    // ───────────────────────────── connecting agents ─────────────────────────────

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static readonly HookTarget[] Targets =
    [
        new("claude", "Claude Code", Path.Combine(Home, ".claude", "settings.json"),
            ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"], 5, false,
            "New Claude Code sessions report here."),
        new("codex", "Codex", Path.Combine(Home, ".codex", "hooks.json"),
            ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Stop", "Interrupt", "SessionEnd"], 5, true,
            "In Codex, run /hooks once and trust HaloBar's hooks."),
        new("gemini", "Gemini CLI", Path.Combine(Home, ".gemini", "settings.json"),
            ["SessionStart", "BeforeAgent", "BeforeTool", "AfterTool", "Notification", "AfterAgent", "SessionEnd"], 5000, false,
            "New Gemini CLI sessions report here."),
    ];

    /// <summary>The agent's settings folder exists, so it has been used on this PC.</summary>
    public static bool Installed(HookTarget t) => Directory.Exists(Path.GetDirectoryName(t.SettingsPath));

    public static bool Connected(HookTarget t)
    {
        try { return File.Exists(t.SettingsPath) && File.ReadAllText(t.SettingsPath).Contains(Marker); }
        catch (IOException) { return false; }
    }

    public bool AnyConnected => Targets.Any(Connected);

    /// <summary>
    /// The command a hook runs: pipe the event JSON to the local API with curl, quietly, with a
    /// two-second cap so it never holds the agent up. "@-" is quoted so cmd, PowerShell and bash
    /// all pass it through; curl.exe ships with Windows. The API answers "{}", which every agent
    /// reads as "carry on".
    /// </summary>
    public string HookCommand(HookTarget t, bool windows) =>
        $"{(windows ? "curl.exe" : "curl")} -s -m 2 -X POST -H \"Content-Type: application/json\" --data-binary \"@-\" \"http://127.0.0.1:{_port}/agent/{t.Id}?{Marker}\""
        + (windows ? "" : " >/dev/null 2>&1 || true");

    /// <summary>Adds HaloBar's hooks to the agent's user settings, after copying the file aside.</summary>
    public string Connect(HookTarget t) => Connect(t, t.SettingsPath);

    /// <summary>Same, into another file (tests use a scratch copy).</summary>
    public string Connect(HookTarget t, string path)
    {
        try
        {
            var root = Load(path);
            if (File.Exists(path)) File.Copy(path, path + ".halobar-backup", overwrite: true);
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            root["hooks"] = hooks;
            foreach (var name in t.Events)
            {
                var list = hooks[name] as JsonArray ?? new JsonArray();
                hooks[name] = list;
                RemoveOurs(list);
                // Claude Code runs hooks through bash even on Windows; Gemini CLI uses the system
                // shell (cmd here); Codex takes a separate Windows command.
                var command = HookCommand(t, windows: t.Id == "gemini" && OperatingSystem.IsWindows());
                var hook = new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = t.Timeout };
                if (t.WindowsCommand) hook["commandWindows"] = HookCommand(t, windows: true);
                list.Add(new JsonObject { ["matcher"] = t.Id == "gemini" ? "*" : "", ["hooks"] = new JsonArray(hook) });
            }
            Save(path, root);
            Log.Write($"Agents: {t.Name} hooks added");
            return $"{t.Name} connected. {t.Note}";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Write($"Agents: could not connect {t.Name}: {ex.Message}");
            return $"Couldn't edit {t.Name}'s settings";
        }
    }

    /// <summary>Removes exactly the hooks <see cref="Connect(HookTarget)"/> added and nothing else.</summary>
    public string Disconnect(HookTarget t) => Disconnect(t, t.SettingsPath);

    public string Disconnect(HookTarget t, string path)
    {
        try
        {
            var root = Load(path);
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
            // A hooks.json that held only HaloBar's hooks goes away entirely.
            if (root.Count == 0 && Path.GetFileName(path) == "hooks.json") File.Delete(path);
            else Save(path, root);
            Log.Write($"Agents: {t.Name} hooks removed");
            return $"{t.Name} disconnected";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Write($"Agents: could not disconnect {t.Name}: {ex.Message}");
            return $"Couldn't edit {t.Name}'s settings";
        }
    }

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject()
            : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidOperationException($"{Path.GetFileName(path)} isn't an object");
    }

    private static void RemoveOurs(JsonArray list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
            if (list[i]?.ToJsonString().Contains(Marker) == true) list.RemoveAt(i);
    }

    private static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
