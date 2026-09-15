# Iris extensions

Status: phases 1 and 2 (loader, runner, hooks, tools, commands, flags, interactive UI) are implemented. The bundled
web-access extension is in progress.

## Decisions

| Topic | Decision |
|-------|----------|
| Language | C# only. No TypeScript/JavaScript. |
| Delivery | `.cs` source files compiled by Iris at load time (Roslyn, in memory), or prebuilt `.dll` assemblies. |
| Dependencies | No package restore. Extensions can use the assemblies Iris ships plus DLLs placed next to the extension. |
| API | pi's extension model (events, hook semantics, tools, commands, shortcuts, flags, providers, UI), expressed in idiomatic C#. |
| Isolation | In-process, full trust. Project-local extensions load only in trusted projects. |
| Built-ins | Extensions can be bundled with Iris (`BuiltinExtensions.Register`) and disabled with `builtinExtensions.<id>: false` in settings. |

## Writing an extension

An extension is a public class implementing `IExtension` with a parameterless constructor. `Register` runs once per
load and wires everything up.

```csharp
using System.ComponentModel;

public sealed class Guardrails : IExtension
{
    public void Register(IExtensionApi iris)
    {
        // Hook: block dangerous shell commands before they run.
        iris.On<ToolCallEvent, ToolCallResult>((e, ctx) =>
            e.ToolName == "bash" && e.Input["command"]?.GetValue<string>().Contains("rm -rf /") == true
                ? ToolCallResult.Blocked("Refusing to delete the filesystem")
                : null);

        // Tool: parameters come from a record; the JSON schema is generated from it.
        iris.RegisterTool(new Tool<CountWordsParams>
        {
            Name = "count_words",
            Label = "Count words",
            Description = "Count the words in a text file",
            ExecuteAsync = async (call, args, ctx) =>
            {
                var text = await File.ReadAllTextAsync(Path.Combine(ctx.Cwd, args.Path), call.CancellationToken);
                return ToolResult.Text($"{text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length} words");
            },
        });

        // Slash command with UI.
        iris.RegisterCommand("greet", new CommandOptions
        {
            Description = "Say hello",
            HandlerAsync = async (args, ctx) =>
            {
                var name = await ctx.UI.InputAsync("Your name?");
                ctx.UI.Notify($"Hello, {name ?? "stranger"}!");
            },
        });

        // Session lifecycle; async handlers use OnAsync.
        iris.On<SessionStartEvent>((e, ctx) => ctx.UI.SetStatus("guardrails", "guarded"));
        iris.OnAsync<AgentEndedEvent>(async (e, ctx) => await File.AppendAllTextAsync("turns.log", $"{e.Messages.Count}\n"));
    }
}

public sealed record CountWordsParams([property: Description("File path, relative to the working directory")] string Path);
```

Source extensions get implicit usings for `System`, `System.Collections.Generic`, `System.IO`, `System.Linq`,
`System.Net.Http`, `System.Threading`, `System.Threading.Tasks`, `System.Text.Json`, `System.Text.Json.Nodes`, `Iris.Ai`,
`Iris.Extensions`, `Iris.Tui`, `Iris.Tui.Components` and `Iris.CodingAgent.Modes.Interactive` (themes), with nullable
reference types enabled.

### Surface

The public API lives in the `Iris.Extensions` namespace of the `Iris.CodingAgent` assembly.

- `On<TEvent>` / `On<TEvent, TResult>` for synchronous handlers, `OnAsync<TEvent>` / `OnAsync<TEvent, TResult>` for
  asynchronous ones. Events: `SessionStartEvent`, `SessionShutdownEvent`, `SessionBefore{Switch,Fork,Compact,Tree}Event`,
  `SessionCompactEvent`, `SessionTreeEvent`, `ModelSelectEvent`, `ThinkingLevelSelectEvent`, `AgentStartedEvent`,
  `AgentEndedEvent`, `AgentIdleEvent`, `TurnStartedEvent`, `TurnEndedEvent`, `MessageStartedEvent`,
  `MessageUpdatedEvent`, `MessageEndedEvent`, `ToolExecution{Started,Updated,Ended}Event`, `ToolCallEvent`,
  `ToolResultEvent`, `InputEvent`, `BeforeAgentStartEvent`, `ContextEvent`, `ResourcesDiscoverEvent`.
- `RegisterTool` (`Tool<TParams>` with a generated schema, or a `ToolDefinition` with a raw schema), `RegisterCommand`,
  `RegisterShortcut`, `RegisterFlag` / `GetFlag`, `RegisterProvider` / `UnregisterProvider`, `RegisterMessageRenderer`.
- Actions: `SendMessage`, `SendUserMessage`, `AppendEntry`, `Set/GetSessionName`, `SetLabel`, `ExecAsync`,
  `GetActiveTools` / `GetAllTools` / `SetActiveTools`, `GetCommands`, `SetModelAsync`, `Get/SetThinkingLevel`, `Events`
  (bus shared between extensions).
- `ExtensionContext`: `UI`, `HasUI`, `Mode`, `Cwd`, `SessionManager`, `ModelRegistry`, `Model`, `IsIdle`, `Abort`,
  `Compact`, `GetContextUsage`, `GetSystemPrompt`, `Shutdown`; command handlers get a `CommandContext` with
  `WaitForIdleAsync` and `ReloadAsync`.
- `ctx.UI`: `SelectAsync`, `ConfirmAsync`, `InputAsync` (cancellable), `EditorAsync`, `Notify`, `SetStatus` (footer),
  `SetWorkingMessage`, `SetWidget` / `ClearWidget` (text lines or a component, above or below the editor), `SetTitle`,
  `CustomAsync<T>` (any `Iris.Tui` component in place of the editor, or as an overlay), `Get/SetEditorText`,
  `PasteToEditor` and `OnTerminalInput`. It is safe to call from any thread. Without a UI (print, json and rpc modes)
  dialogs return null/false and the rest does nothing.
- UI state (statuses, widgets, overlays, shortcuts, input listeners) is cleared on `/reload`, `/new`, `/resume` and
  `/fork`; extensions set it again from `SessionStartEvent`. Flag values survive `/reload`.

Hook semantics follow pi: extensions run in load order; `tool_call` stops at the first block and handler errors
propagate (a broken guard cannot let a tool run); `tool_result` and `message_end` chain modifications;
`session_before_*` stops at the first cancel; other throwing handlers are reported and the next handler still runs.
A blocked tool call is not executed and does not emit `tool_result`.

## Layout on disk

| Location | Loads |
|----------|-------|
| `~/.iris/agent/extensions/<name>.cs` | Single-file extension. |
| `~/.iris/agent/extensions/<name>.dll` | Prebuilt extension. |
| `~/.iris/agent/extensions/<name>/` | `<name>/<name>.dll` if present, otherwise every `*.cs` file in the folder (excluding `bin`/`obj`) compiled as one assembly. DLLs next to it are resolvable references. |
| `.iris/extensions/...` | Same, project-local (trusted projects only). |
| `extensions` settings / `-e <path>` | A file or folder. |

## Loading

- **Source:** Roslyn compiles each extension into an in-memory assembly with a portable PDB, referencing the runtime,
  the assemblies shipped with Iris and any sibling DLLs. Compiler errors are reported as `file(line,col): error CSxxxx`.
- **Cache:** compiled assemblies are cached under `~/.iris/agent/cache/extensions/<sha256>.dll` keyed by the Iris
  version and build, the sources and sibling DLLs, so unchanged extensions skip compilation.
- **Assemblies:** each extension loads into its own collectible `AssemblyLoadContext`; Iris assemblies are shared with
  the host so types match. `/reload` unloads and reloads everything.
- **Publishing constraint:** compiling against the runtime needs its assemblies on disk, so Iris must not be published
  as a single-file or native-AOT app.

## Code

- `src/Iris.CodingAgent/Extensions/Api`: the public API (`IExtension`, `IExtensionApi`, events and results, `Tool<T>`,
  `ExtensionContext`, `IExtensionUI`).
- `src/Iris.CodingAgent/Core/Extensions`: loader (Roslyn, DLL, cache, built-ins) and runner (implements
  `IExtensionRunner`, which `AgentSession` calls).

## Phases

1. **Core (done):** loader (source, DLL, cache, reload), runner with the hooks AgentSession calls, tools, commands,
   flags, session/agent/turn/message/tool events, `ctx` basics, load errors as diagnostics.
2. **Interactive UI (done):** dialogs, notify, status, widgets, working message, title, custom components and overlays,
   shortcuts, message renderers, tool `RenderCall`/`RenderResult`, terminal input.
3. **Web access:** bundled port of pi-web-access (`web_search`, `fetch_content`, `get_search_content`), later the
   curator as a TUI overlay.
4. **Remaining:** RPC-mode UI requests, entry renderers, markdown transformers, autocomplete providers, header/footer
   replacement, moving the built-in llama.cpp provider onto the extension API, example extensions.
