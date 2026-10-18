# MCP / CLI Conventions

## MCP tool naming

Tool names are derived from the C# method name by the MCP SDK's (`ModelContextProtocol`) default convention: a PascalCase method (e.g. `GetRobotStudioProcessStatus`) becomes a snake_case tool name (`get_robot_studio_process_status`) via `JsonNamingPolicy.SnakeCaseLower`. This repo doesn't override that — no tool sets `[McpServerTool(Name = ...)]` — so the method name and the wire name never drift independently.

Every tool method carries a `[McpServerTool]` attribute with per-tool annotations:

- `ReadOnly = true` for tools that only inspect state (`VerifyAddinMcpReady`, status/list tools).
- `Destructive = true` for tools with side effects a caller should think twice about (`StartRobotStudioProcess`, package install/uninstall).
- `Title = "..."` for a short human-readable label distinct from the tool name/description.

## Result convention

Every tool method here returns a bare `object` (or `Task<object>`) and reports *business* failure through a conventional boolean flag in that returned object — `ok`, `started`, `installed`, `uninstalled`, `listed`, `stopped`, `ready`, `reachable` — rather than throwing. Throwing is reserved for truly exceptional conditions; an expected failure (package already installed, add-in not ready yet, process not found) comes back as `{ "ok": false, ... }` with explanation fields alongside it.

That convention predates MCP in this codebase — it's also what unit tests and the CLI read directly. The gap is that the MCP SDK itself only sets `CallToolResult.IsError` when a tool *throws*; a bare `ok:false` payload returned normally looks like a successful call to any MCP client, including the official C# client, unless something translates the convention into the protocol-level signal.

`ConventionalToolResultFilter` (registered once in `McpStdioServerHost`, via `WithRequestFilters().AddCallToolFilter(...)`) is that translation: after each tool call, it inspects the result and sets `IsError = true` if any of the known success-flag names is present and `false`. Deliberately, it reads **`CallToolResult.Content`'s text block**, not `StructuredContent`.

**Why not `StructuredContent`:** `UseStructuredContent` on a method whose declared return type is the bare `object` (every tool in this project) makes the SDK wrap the actual payload one level deeper under a synthetic top-level `result` key, because it can't generate a real JSON schema from `object` — e.g. `{"ok":false}` goes out on the wire as `{"result":{"ok":false}}`. The official C# client unwraps that transparently, so it's invisible there, but it breaks any client that doesn't know to look for that wrapper (this is exactly the bug this repo's own smoke tests caught, since they parse the wire JSON directly rather than going through the C# SDK's client). The text content block carries the original, unwrapped JSON, which is also the shape these tools returned before MCP was added at all.

**Consequence:** don't set `UseStructuredContent` on these tools unless and until their return types stop being bare `object` — doing so while the return type is still `object` reintroduces the synthetic `result` wrapper for any client that isn't the official SDK's own.

## CLI conventions (watchdog)

- Parsing: `System.CommandLine`, built once per invocation in `RobotStudioCommandLine.BuildRootCommand()`. Commands and shared option groups (e.g. the `ready`-related options reused by both `start --wait-ready` and `ready`) are composed from the same `Option<T>` instances rather than duplicated.
- Every JSON envelope a command prints carries a `schemaVersion` field (currently `1`, a `const int` in `RobotStudioCommandLine`) alongside `command` and the command's own result shape — a consumer can branch on `schemaVersion` if the shape ever changes.
- Exit codes: `0` on success, `1` for a reported business failure (the command ran, but its own `ok`/equivalent flag came back false), and parse errors (unrecognized option, missing required argument) are handled by `System.CommandLine` itself before a command body ever runs.
- Every command gets `--help` for free from `System.CommandLine`; a bare `help` is kept as a back-compat alias that's rewritten to `--help` before parsing.
- `--version` reports the watchdog's own assembly/file version.
- No-argument invocation falls back to running the stdio MCP server directly (`McpStdioServerHost.RunAsync`), for backward compatibility with clients that launch the watchdog with no arguments expecting an MCP server. The explicit `mcp`/`serve` command is the discoverable equivalent of that same fallback, for anyone reading `--help` output instead of relying on tribal knowledge.
