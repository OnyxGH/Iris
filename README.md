# Iris

Iris is a terminal coding agent for .NET developers. It reads and edits files, runs shell commands and works with the
model provider of your choice.

## Install

Iris is a .NET tool and needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet tool install -g iris-agent
```

Then run `iris` in a project directory. Update with `iris update` (or `dotnet tool update -g iris-agent`).

## Getting started

1. Run `iris`.
2. Sign in to a provider with `/login` (API keys for Anthropic, OpenAI, Google, OpenRouter and many more), or point
   Iris at a local [llama.cpp](https://github.com/ggml-org/llama.cpp) server with `/login llama.cpp`.
3. Pick a model with `/model` and start asking.

Non-interactive use:

```bash
iris -p "Summarize the changes in the last commit"
```

## Features

- **Tools:** read, write, edit, bash (and PowerShell on Windows), grep, find, ls.
- **Sessions:** saved as JSONL, resumable (`iris --resume`), with branching (`/tree`), forking and compaction.
- **Customization:** skills, prompt templates, themes, keybindings and settings (`/settings`, `iris config`).
- **Modes:** interactive, print (`-p`), JSON event stream (`--mode json`) and an RPC mode for editor integrations.
- **Web access:** `web_search`, `fetch_content` (readable pages, PDFs, GitHub repositories) and `get_search_content`, working without API keys; see [docs/WEB-ACCESS.md](docs/WEB-ACCESS.md).
- **llama.cpp:** list, load and unload router models with `/llama`.

Run `iris --help` for all options and `/hotkeys` inside Iris for keyboard shortcuts.

## Configuration

User settings, credentials and sessions live in `~/.iris/agent/`. Project-specific settings and resources go in
`.iris/` inside the project and only load once you trust the project.

## Extensions

Extensions are written in C#, as single `.cs` files, folders of sources, or prebuilt assemblies, and placed in
`~/.iris/agent/extensions/`. See [docs/EXTENSIONS.md](docs/EXTENSIONS.md).

## Building from source

```bash
dotnet build Iris.slnx
dotnet test Iris.slnx
```

## License

MIT. Iris is derived from [pi](https://github.com/earendil-works/pi) by Mario Zechner, and its web access extension from
[pi-web-access](https://github.com/nicobailon/pi-web-access) by Nico Bailon; see [LICENSE](LICENSE).
