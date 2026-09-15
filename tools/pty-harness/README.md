# PTY harness

Drives `pisharp` (or real pi) in a Windows ConPTY so interactive mode can be tested without a human at the keyboard.

- `pty.cs`: file-based .NET app. `dotnet run pty.cs -- <outFile> <cols> <rows> <script> -- <exe> [args...]`.
  Script commands: `wait <ms>`, `send <text>` (escapes `\e \r \n \t \xNN`), `until <text> <timeoutMs>`,
  `screen <label>` (prints the emulated screen), `waitgone <text> <timeoutMs>` (waits until the screen no longer shows text). Raw output goes to `<outFile>`, ANSI-stripped output to `<outFile>.txt`.
- `fake-openai-server.mjs`: OpenAI-compatible streaming server on port 8690 (reasoning, a `bash` tool call, then markdown).
  Point an agent dir at it with `fake-models.json` as its `models.json`.
- `compare-debug-logs.mjs <pi-debug.log> <pisharp-debug.log>`: compares `/debug` render dumps line by line, ANSI included.

Parity run: start the fake server, then run the same script against pi (`PI_CODING_AGENT_DIR`, `PI_OFFLINE=1`,
`node .../pi-coding-agent/dist/bundle/cli.js`) and pisharp (`PISHARP_CODING_AGENT_DIR`), each script ending with `/debug`.
