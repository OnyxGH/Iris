using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Ai;
using PiSharp.Ai.Json;
using PiSharp.CodingAgent.Core;
using PiSharp.CodingAgent.Core.Compaction;
using PiSharp.CodingAgent.Utils;

namespace PiSharp.CodingAgent.Modes;

/// <summary>
/// Headless JSONL protocol over stdin/stdout. Port of modes/rpc/rpc-mode.ts. Extension UI requests are not emitted yet
/// because no extension runtime exists.
/// </summary>
public sealed class RpcMode
{
    private readonly AgentSessionRuntime _runtime;
    private readonly TextWriter _stdout;
    private readonly object _writeLock = new();
    private AgentSession _session;
    private IDisposable? _subscription;
    private bool _shuttingDown;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RpcMode(AgentSessionRuntime runtime, TextWriter stdout)
    {
        _runtime = runtime;
        _stdout = stdout;
        _session = runtime.Session;
    }

    /// <summary>Serialize one LF-framed JSON record (JSON.stringify + "\n").</summary>
    public static string SerializeJsonLine(JsonNode node) => node.ToJsonString(PiJson.Options) + "\n";

    private void Output(JsonNode node)
    {
        lock (_writeLock)
        {
            _stdout.Write(SerializeJsonLine(node));
            _stdout.Flush();
        }
    }

    private static JsonObject Success(string? id, string command, JsonNode? data = null, bool includeData = false)
    {
        var obj = new JsonObject();
        if (id is not null) obj["id"] = id;
        obj["type"] = "response";
        obj["command"] = command;
        obj["success"] = true;
        if (includeData || data is not null) obj["data"] = data;
        return obj;
    }

    private static JsonObject Error(string? id, string command, string message)
    {
        var obj = new JsonObject();
        if (id is not null) obj["id"] = id;
        obj["type"] = "response";
        obj["command"] = command;
        obj["success"] = false;
        obj["error"] = message;
        return obj;
    }

    private async Task RebindSessionAsync()
    {
        _session = _runtime.Session;
        await _session.BindExtensionsAsync(err => Output(new JsonObject
        {
            ["type"] = "extension_error", ["extensionPath"] = err.ExtensionPath, ["event"] = err.Event, ["error"] = err.Error,
        }));
        _subscription?.Dispose();
        _subscription = _session.Subscribe(evt => Output(JsonEvents.ToJson(evt)));
    }

    public static Task<int> RunAsync(AgentSessionRuntime runtime, TextReader stdin, TextWriter stdout) =>
        new RpcMode(runtime, stdout).RunCoreAsync(stdin);

    private async Task<int> RunCoreAsync(TextReader stdin)
    {
        _runtime.SetRebindSession(_ => RebindSessionAsync());
        await RebindSessionAsync();

        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            ShellUtils.KillTrackedDetachedChildren();
            _ = ShutdownAsync(143);
        });

        _ = Task.Run(async () =>
        {
            foreach (var line in ReadJsonlLines(stdin))
            {
                _ = HandleInputLineAsync(line);
            }
            await ShutdownAsync(0);
        });

        return await _exit.Task;
    }

    /// <summary>Strict LF-only JSONL framing (U+2028/U+2029 are valid inside JSON strings). A trailing CR is dropped.</summary>
    public static IEnumerable<string> ReadJsonlLines(TextReader reader)
    {
        var buffer = new StringBuilder();
        var chunk = new char[4096];
        int read;
        while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (chunk[i] != '\n')
                {
                    buffer.Append(chunk[i]);
                    continue;
                }
                yield return TrimCr(buffer.ToString());
                buffer.Clear();
            }
        }
        if (buffer.Length > 0) yield return TrimCr(buffer.ToString());

        static string TrimCr(string line) => line.EndsWith('\r') ? line[..^1] : line;
    }

    private async Task ShutdownAsync(int exitCode)
    {
        if (_shuttingDown)
        {
            _exit.TrySetResult(exitCode);
            return;
        }
        _shuttingDown = true;
        _subscription?.Dispose();
        try
        {
            await _runtime.DisposeAsync();
        }
        finally
        {
            lock (_writeLock) _stdout.Flush();
            _exit.TrySetResult(exitCode);
        }
    }

    private async Task HandleInputLineAsync(string line)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            Output(Error(null, "parse", $"Failed to parse command: {ex.Message}"));
            return;
        }

        if (parsed is not JsonObject command)
        {
            Output(Error(null, "parse", "Failed to parse command: expected an object"));
            return;
        }
        // Extension UI responses have no pending requests until extensions exist.
        if (PiJson.GetString(command["type"]) == "extension_ui_response") return;

        var id = PiJson.GetString(command["id"]);
        var type = PiJson.GetString(command["type"]) ?? "";
        try
        {
            if (await HandleCommandAsync(command, id, type) is { } response) Output(response);
        }
        catch (Exception ex)
        {
            Output(Error(id, type, ex.Message));
        }
    }

    private static List<ImageContent>? Images(JsonObject command) =>
        command["images"] is JsonArray arr ? arr.Select(n => PiJson.Deserialize<ContentBlock>(n)).OfType<ImageContent>().ToList() : null;

    private static JsonNode? ModelNode(Model? model) => model is null ? null : PiJson.ToNode(model);

    private static JsonObject StatsNode(SessionStats stats)
    {
        var obj = new JsonObject();
        if (stats.SessionFile is not null) obj["sessionFile"] = stats.SessionFile;
        obj["sessionId"] = stats.SessionId;
        obj["userMessages"] = stats.UserMessages;
        obj["assistantMessages"] = stats.AssistantMessages;
        obj["toolCalls"] = stats.ToolCalls;
        obj["toolResults"] = stats.ToolResults;
        obj["totalMessages"] = stats.TotalMessages;
        obj["tokens"] = new JsonObject
        {
            ["input"] = stats.Tokens.Input, ["output"] = stats.Tokens.Output, ["cacheRead"] = stats.Tokens.CacheRead,
            ["cacheWrite"] = stats.Tokens.CacheWrite, ["total"] = stats.Tokens.Total,
        };
        obj["cost"] = stats.Cost;
        if (stats.ContextUsage is { } usage)
        {
            obj["contextUsage"] = new JsonObject { ["tokens"] = usage.Tokens, ["contextWindow"] = usage.ContextWindow, ["percent"] = usage.Percent };
        }
        return obj;
    }

    private static JsonNode TreeNode(SessionTreeNode node)
    {
        var obj = new JsonObject
        {
            ["entry"] = PiJson.ToNode<FileEntry>(node.Entry),
            ["children"] = new JsonArray(node.Children.Select(TreeNode).ToArray()),
        };
        if (node.Label is not null) obj["label"] = node.Label;
        if (node.LabelTimestamp is not null) obj["labelTimestamp"] = node.LabelTimestamp;
        return obj;
    }

    private static JsonObject CompactionResultNode(CompactionResult result)
    {
        var obj = new JsonObject { ["summary"] = result.Summary, ["firstKeptEntryId"] = result.FirstKeptEntryId, ["tokensBefore"] = result.TokensBefore };
        if (result.EstimatedTokensAfter is { } after) obj["estimatedTokensAfter"] = after;
        if (result.Usage is not null) obj["usage"] = PiJson.ToNode(result.Usage);
        if (result.Details is not null) obj["details"] = result.Details.DeepClone();
        return obj;
    }

    private async Task<JsonObject?> HandleCommandAsync(JsonObject command, string? id, string type)
    {
        var session = _session;
        switch (type)
        {
            case "prompt":
            {
                // Respond only after preflight succeeds; queued and handled prompts count as success.
                var preflightSucceeded = false;
                _ = session.PromptAsync(PiJson.GetString(command["message"]) ?? "", new PromptOptions
                {
                    Images = Images(command),
                    StreamingBehavior = PiJson.GetString(command["streamingBehavior"]),
                    Source = "rpc",
                    PreflightResult = ok =>
                    {
                        if (!ok) return;
                        preflightSucceeded = true;
                        Output(Success(id, "prompt"));
                    },
                }).ContinueWith(t =>
                {
                    if (t.IsFaulted && !preflightSucceeded) Output(Error(id, "prompt", t.Exception!.GetBaseException().Message));
                }, TaskScheduler.Default);
                return null;
            }
            case "steer":
                await session.SteerAsync(PiJson.GetString(command["message"]) ?? "", Images(command), "rpc");
                return Success(id, "steer");
            case "follow_up":
                await session.FollowUpAsync(PiJson.GetString(command["message"]) ?? "", Images(command), "rpc");
                return Success(id, "follow_up");
            case "abort":
                await session.AbortAsync();
                return Success(id, "abort");
            case "clear_queue":
            {
                var (steering, followUp) = session.ClearQueue();
                return Success(id, "clear_queue", new JsonObject { ["steering"] = PiJson.ToNode(steering), ["followUp"] = PiJson.ToNode(followUp) });
            }
            case "new_session":
            {
                var ok = await _runtime.NewSessionAsync(PiJson.GetString(command["parentSession"]));
                if (ok) await RebindSessionAsync();
                return Success(id, "new_session", new JsonObject { ["cancelled"] = !ok });
            }
            case "get_state":
            {
                var state = new JsonObject();
                if (session.Model is { } model) state["model"] = ModelNode(model);
                state["thinkingLevel"] = session.ThinkingLevel.ToWire();
                state["isStreaming"] = session.IsStreaming;
                state["isCompacting"] = session.IsCompacting;
                state["steeringMode"] = session.SteeringMode.ToWire();
                state["followUpMode"] = session.FollowUpMode.ToWire();
                if (session.SessionFile is not null) state["sessionFile"] = session.SessionFile;
                state["sessionId"] = session.SessionId;
                if (session.SessionName is not null) state["sessionName"] = session.SessionName;
                state["autoCompactionEnabled"] = session.AutoCompactionEnabled;
                state["messageCount"] = session.Messages.Count;
                state["pendingMessageCount"] = session.PendingMessageCount;
                return Success(id, "get_state", state);
            }
            case "set_model":
            {
                var provider = PiJson.GetString(command["provider"]);
                var modelId = PiJson.GetString(command["modelId"]);
                var model = session.ModelRuntime.AvailableSnapshot.FirstOrDefault(m => m.Provider == provider && m.Id == modelId);
                if (model is null) return Error(id, "set_model", $"Model not found: {provider}/{modelId}");
                await session.SetModelAsync(model);
                return Success(id, "set_model", ModelNode(model));
            }
            case "cycle_model":
            {
                var result = await session.CycleModelAsync();
                return Success(id, "cycle_model", result is null ? null : new JsonObject
                {
                    ["model"] = ModelNode(result.Model), ["thinkingLevel"] = result.ThinkingLevel.ToWire(), ["isScoped"] = result.IsScoped,
                }, includeData: true);
            }
            case "get_available_models":
                return Success(id, "get_available_models", new JsonObject { ["models"] = new JsonArray(session.ModelRuntime.AvailableSnapshot.Select(ModelNode).ToArray()) });
            case "set_thinking_level":
                if (!ThinkingLevels.TryParse(PiJson.GetString(command["level"]), out var level)) return Error(id, type, $"Invalid thinking level: {command["level"]}");
                session.SetThinkingLevel(level);
                return Success(id, "set_thinking_level");
            case "cycle_thinking_level":
            {
                var next = session.CycleThinkingLevel();
                return Success(id, "cycle_thinking_level", next is null ? null : new JsonObject { ["level"] = next.Value.ToWire() }, includeData: true);
            }
            case "get_available_thinking_levels":
                return Success(id, "get_available_thinking_levels", new JsonObject { ["levels"] = new JsonArray(session.GetAvailableThinkingLevels().Select(l => (JsonNode)l.ToWire()).ToArray()) });
            case "set_steering_mode":
                session.SetSteeringMode(QueueModes.Parse(PiJson.GetString(command["mode"])));
                return Success(id, "set_steering_mode");
            case "set_follow_up_mode":
                session.SetFollowUpMode(QueueModes.Parse(PiJson.GetString(command["mode"])));
                return Success(id, "set_follow_up_mode");
            case "compact":
                return Success(id, "compact", CompactionResultNode(await session.CompactAsync(PiJson.GetString(command["customInstructions"]))));
            case "set_auto_compaction":
                session.SetAutoCompactionEnabled(PiJson.GetBool(command["enabled"]) ?? false);
                return Success(id, "set_auto_compaction");
            case "set_auto_retry":
                session.SetAutoRetryEnabled(PiJson.GetBool(command["enabled"]) ?? false);
                return Success(id, "set_auto_retry");
            case "abort_retry":
                session.AbortRetry();
                return Success(id, "abort_retry");
            case "bash":
            {
                var result = await session.ExecuteBashAsync(PiJson.GetString(command["command"]) ?? "", null, PiJson.GetBool(command["excludeFromContext"]) ?? false, id);
                return Success(id, "bash", PiJson.ToNode(result));
            }
            case "abort_bash":
                session.AbortBash();
                return Success(id, "abort_bash");
            case "get_session_stats":
                return Success(id, "get_session_stats", StatsNode(session.GetSessionStats()));
            case "export_html":
                return Error(id, "export_html", "HTML export is not available in PiSharp yet");
            case "switch_session":
            {
                var ok = await _runtime.SwitchSessionAsync(PiJson.GetString(command["sessionPath"]) ?? "");
                if (ok) await RebindSessionAsync();
                return Success(id, "switch_session", new JsonObject { ["cancelled"] = !ok });
            }
            case "fork":
            {
                var (cancelled, text) = await _runtime.ForkAsync(PiJson.GetString(command["entryId"]) ?? "");
                if (!cancelled) await RebindSessionAsync();
                var data = new JsonObject();
                if (text is not null) data["text"] = text;
                data["cancelled"] = cancelled;
                return Success(id, "fork", data);
            }
            case "clone":
            {
                if (session.SessionManager.LeafId is not { } leafId) return Error(id, "clone", "Cannot clone session: no current entry selected");
                var (cancelled, _) = await _runtime.ForkAsync(leafId, "at");
                if (!cancelled) await RebindSessionAsync();
                return Success(id, "clone", new JsonObject { ["cancelled"] = cancelled });
            }
            case "get_fork_messages":
                return Success(id, "get_fork_messages", new JsonObject
                {
                    ["messages"] = new JsonArray(session.GetUserMessagesForForking().Select(m => (JsonNode)new JsonObject { ["entryId"] = m.EntryId, ["text"] = m.Text }).ToArray()),
                });
            case "get_entries":
            {
                var entries = session.SessionManager.GetEntries();
                if (PiJson.GetString(command["since"]) is { } since)
                {
                    var index = entries.FindIndex(e => e.Id == since);
                    if (index == -1) return Error(id, "get_entries", $"Entry not found: {since}");
                    entries = entries.Skip(index + 1).ToList();
                }
                return Success(id, "get_entries", new JsonObject
                {
                    ["entries"] = new JsonArray(entries.Select(e => PiJson.ToNode<FileEntry>(e)).ToArray()),
                    ["leafId"] = session.SessionManager.LeafId,
                });
            }
            case "get_tree":
                return Success(id, "get_tree", new JsonObject
                {
                    ["tree"] = new JsonArray(session.SessionManager.GetTree().Select(TreeNode).ToArray()),
                    ["leafId"] = session.SessionManager.LeafId,
                });
            case "get_last_assistant_text":
                return Success(id, "get_last_assistant_text", new JsonObject { ["text"] = session.GetLastAssistantText() });
            case "set_session_name":
            {
                var name = (PiJson.GetString(command["name"]) ?? "").Trim();
                if (name.Length == 0) return Error(id, "set_session_name", "Session name cannot be empty");
                session.SetSessionName(name);
                return Success(id, "set_session_name");
            }
            case "get_messages":
                return Success(id, "get_messages", new JsonObject { ["messages"] = new JsonArray(session.Messages.Select(m => PiJson.ToNode<Message>(m)).ToArray()) });
            case "get_commands":
            {
                var commands = new JsonArray();
                foreach (var cmd in session.ExtensionRunner.GetRegisteredCommands())
                {
                    commands.Add(CommandNode(cmd.InvocationName, cmd.Description, "extension", cmd.SourceInfo));
                }
                foreach (var template in session.PromptTemplates) commands.Add(CommandNode(template.Name, template.Description, "prompt", template.SourceInfo));
                foreach (var skill in session.ResourceLoader.GetSkills().Skills) commands.Add(CommandNode($"skill:{skill.Name}", skill.Description, "skill", skill.SourceInfo));
                return Success(id, "get_commands", new JsonObject { ["commands"] = commands });
            }
            default:
                return Error(id, type, $"Unknown command: {type}");
        }
    }

    private static JsonObject CommandNode(string name, string? description, string source, SourceInfo sourceInfo)
    {
        var obj = new JsonObject { ["name"] = name };
        if (description is not null) obj["description"] = description;
        obj["source"] = source;
        obj["sourceInfo"] = PiJson.ToNode(sourceInfo);
        return obj;
    }
}
