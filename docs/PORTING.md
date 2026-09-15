# PiSharp porting plan

PiSharp is a C# (.NET 10) port of [pi](https://pi.dev) — the `@earendil-works/pi-coding-agent` CLI and the
packages it depends on. The reference TypeScript source lives in `reference/pi` (v0.85.1).

Goal: behave the same as pi. Same CLI flags, slash commands, keybindings, TUI layout, session JSONL format,
settings/auth/models JSON formats, system prompt, tools, compaction, RPC/JSON protocols.

## Decisions

| Topic | Decision |
|-------|----------|
| Config dir | Same file formats, but default to `~/.pisharp/agent` and project `.pisharp/` so the port never touches real pi data. |
| Providers | Core APIs first: `openai-completions`, `anthropic-messages`, `openai-responses`, `google-generative-ai`, faux. Then Bedrock, Vertex, Mistral, Codex, Azure, OAuth logins. |
| In scope | Interactive (regular TUI), print, json, rpc modes; HTML export and `/share`; llama.cpp `/llama`. |
| Out of scope (for now) | `--tui-mode fullscreen`, `packages/{server,client,protocol,chord,telemetry,evals}`, `coding-agent/src/experimental`. |
| Extensions | Deferred. To be designed together with the user (TypeScript extensions cannot run as-is). |
| HTTP | Raw `HttpClient` + SSE parsing, no vendor SDKs, so request bodies match pi exactly. SDK error message formats are emulated because overflow/retry detection depends on them. |
| JSON | `System.Text.Json` with `JsonNode` for dynamic data (tool args, schemas, details, compat). Messages/content use role/type discriminators via extensible converters. |
| Abort | `AbortSignal` → `CancellationToken`. |
| Tool schemas | TypeBox schemas → plain JSON Schema `JsonObject`s. |

## Project map

| pi package | PiSharp project |
|------------|-----------------|
| `packages/ai` (pi-ai) | `src/PiSharp.Ai` |
| `packages/agent` (pi-agent-core: `agent.ts`, `agent-loop.ts`, compaction, branch summarization, tools utils) | `src/PiSharp.Agent` |
| `packages/tui` (pi-tui) | `src/PiSharp.Tui` |
| `packages/coding-agent` | `src/PiSharp.CodingAgent` (library) + `src/PiSharp.Cli` (executable `pisharp`) |

## Progress

### PiSharp.Ai
- [x] Core types (messages, content, model, usage, options, events), JSON converters
- [x] Event stream (`AssistantMessageEventStream`, lazy streams)
- [x] Utils: partial JSON, repair, surrogate sanitize, short hash, uuidv7, estimate, overflow, retry classification, provider HTTP/SSE/retry, error formatting
- [x] transform-messages, simple-options, constrained-sampling
- [x] `openai-completions`
- [x] Provider / Models abstraction, auth (api key, env), credential + models stores
- [x] Built-in catalog (embedded JSON from pi's generated data) + provider factories (radius omitted)
- [x] Faux provider (tests; no deferred responses)
- [x] `anthropic-messages`
- [x] `openai-responses` (+ shared)
- [x] `google-generative-ai` (+ shared)
- [x] Tool argument validation (JSON Schema subset validator + coercion)
- [x] `PiAi` compat facade (api registry dispatch, env API keys)
- [ ] Later: `azure-openai-responses`, `openai-codex-responses`, `mistral-conversations`, `bedrock-converse-stream`, `google-vertex`, `pi-messages`, OAuth flows, images API, deferred responses
- [~] Unit tests (core subset in tests/PiSharp.Ai.Tests)

### PiSharp.Agent
- [x] `Agent`, agent loop, events, tool execution, steering/follow-up queues
- [x] Compaction + branch summarization (in CodingAgent/Core/Compaction, as in pi; fixture-tested)
- [x] Truncation/shell-output utilities used by tools (live in CodingAgent, as in pi)

### PiSharp.Tui
- [x] Terminal abstraction (Windows console VT / POSIX termios), stdin buffer, key parsing, keybindings, UiDispatcher event loop
- [x] Differential renderer (main screen), overlays
- [x] Components: text, box, spacer, markdown (marked 18 lexer port + LaTeX), editor, input, select list, settings list, loader, image
- [x] Autocomplete, fuzzy matching, word navigation (ICU word segmentation approximated; Han/kana runs grouped)
- [x] Parity tests against pi-tui fixtures (tests/PiSharp.Tui.Tests, generator: scratchpad gen-tui-fixtures.mjs)
- [ ] Mouse / alt-screen (fullscreen mode is out of scope)

### PiSharp.CodingAgent / Cli
- [x] Config paths, settings manager, auth storage, models.json, model registry/runtime/resolver
- [x] Session manager (JSONL tree format), migrations
- [x] Tools: read, bash, powershell, edit, write, grep, find, ls
- [x] Images: SkiaSharp codec (decode, EXIF orientation, resize to 2000x2000 / 4.5MB with pi's PNG/JPEG quality steps, PNG conversion, kitty PNG conversion)
- [x] System prompt, context files, skills, prompt templates, slash commands, resource loader (fixture-tested against pi)
- [x] Package manager: npm/git/local package sources, install/remove/update, update checks, filters and autoload deltas (git URL parsing fixture-tested against pi's hosted-git-info)
- [x] Theme JSON validation (messages fixture-tested against pi's typebox validator)
- [x] HTTP proxy (undici EnvHttpProxyAgent semantics, httpProxy setting), version check (configurable URL), package update notifications
- [x] AgentSession (+ compaction, retry, bash execution, branching), SDK factory, session runtime/services, project trust store (extension hooks go through IExtensionRunner; NullExtensionRunner for now)
- [x] CLI args, main, print/json/rpc/interactive modes, --resume picker, startup trust prompt, `auth`, `install`/`remove`/`update`/`list`, `config` TUI (--export waits for HTML export)
- [x] Interactive mode (Modes/Interactive: InteractiveMode + .Commands partial), components, themes, footer, selectors, settings
- [x] Project trust (store, startup prompt, /trust)
- [ ] HTML export, `/share`
- [x] llama.cpp: built-in `llama.cpp` provider (Extensions/Llama, registered directly on each ModelRuntime) and `/llama` manager (list, load/unload; model downloading intentionally left out)
- [x] `/login` (auth-type selector, provider selector, API-key dialogs) and `/logout`
- [ ] Extensions (design TBD)

## Resume here

Last stopping point: interactive TUI mode is done and paused for user review. All test projects green (Ai 40, Agent 10,
Tui 8, CodingAgent 25). Interactive mode was driven through ConPTY with tools/pty-harness: startup header, autocomplete,
model/thinking/settings/scoped-models/tree/session/trust selectors, `!`/`!!` bash, streaming with thinking + tool calls +
markdown, retry countdown, Ctrl+O/Ctrl+T toggles, double-Escape, /name /export(jsonl) /reload /new /hotkeys /session /debug,
`--resume` picker, startup trust prompt, exit with resume hint. Against a fake streaming server, `/debug` render dumps from pi
and pisharp are byte-identical (ANSI included) from the first user message to the footer.

Threading model: interactive mode runs on a UiDispatcher thread (StartupUi.RunOnUiThreadAsync). Ai/Agent/CodingAgent no longer
use ConfigureAwait(false) (except AuthStorage/FileLock), so session work started from the UI thread stays on it, matching
Node's single thread. Print/rpc modes have no synchronization context and are unaffected.

To try it without touching ~/.pisharp: set PISHARP_CODING_AGENT_DIR to a scratch dir containing a models.json, then run
`src/PiSharp.Cli/bin/Debug/net10.0/pisharp.exe` (interactive) or add `--model llama/Iris -p "..."`.

Interactive-mode deviations from pi (intentional or pending):
- Syntax highlighting uses VS Code TextMate grammars (TextMateSharp) with Dark Modern token colors, and Light Modern for light
  themes; languages without a bundled grammar use the heuristic tokenizer with the same palette. pi uses highlight.js with the
  theme's syntax* colors. Mermaid diagrams are not rendered.
- Version checks read PISHARP_LATEST_VERSION_URL (same JSON shape as pi's endpoint) and are skipped when unset; `update --self`
  reports that PiSharp cannot self-update. The changelog link in the update notice is omitted.
- Encoded image bytes differ from Photon's (different codec); dimensions, size limits and hints match.
- /login works for API-key providers and llama.cpp; OAuth providers (Anthropic, Copilot, Codex, Kimi, OpenRouter) report "login is not yet supported" because the OAuth flows are not ported. /share and HTML /export show errors.
- After `/login llama.cpp`, guidance waits for the catalog refresh (pi reports "no models are loaded" before refreshing) and is shown as a status when models are loaded.
- Fullscreen TUI mode (tui-mode setting) is not supported; the setting stays "regular".
- Extension UI hooks (widgets, custom editors/headers/footers, shortcuts, terminal input listeners) are not wired, pending the extension design.
- Easter egg commands, tmux keyboard check and install telemetry are skipped.
- Ctrl+Z suspend is unsupported on Windows (status message).

Recently done (fixture-tested against the installed pi where noted):
- PiSharp.Tui (parity tests), interactive components/selectors/theme, InteractiveMode, StartupUi, Main wiring.
- Tools, ToolsManager, image mime sniffing (fixtures).
- Skills, PromptTemplates, SystemPrompt, PackageManager.ResolveAsync, DefaultResourceLoader (fixtures).
- Compaction + branch summarization (fixtures).
- BashExecutor, AgentSession (partial classes: core, Models, Compaction, Runtime), Sdk.CreateAgentSessionAsync,
  AgentSessionRuntime/Services, ProjectTrustStore, CliArgs, Main, PrintMode, JsonEvents, ModelLister, Fuzzy.

Not yet ported / known gaps:
- Extensions: IExtensionRunner seam exists (Core/Extensions/ExtensionRunner.cs); loader/runtime design pending with the user.
  Discovered extension entries currently produce a "not loaded" warning.
- Session HTML export, OAuth login flows, migrations, settings diagnostics, project-trust extension hooks in commands.
- npm/git installs are only verified with local packages so far (no network installs run).
- No AgentSession unit tests yet (verified end to end instead); faux-provider tests for retry/compaction flows are worth adding.

Next steps, in order:
1. User testing of interactive mode, /login and /llama against their llama-server (verified: login, catalog listing, /model, prompting; load/unload/download not exercised to avoid disturbing the user's server).
2. HTML export + /share, then --export.
3. Extension design discussion with the user.
4. OAuth login flows.

Open questions to raise with the user when relevant:
- Where PiSharp releases will be published (for PISHARP_LATEST_VERSION_URL and a real self-update).
- Extension model (their real setup uses pi-localllm-provider, pi-mcp-adapter, ...).
