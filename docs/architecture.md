# Architecture

## Component map

| Project | Runs where | TFM | Role |
|---|---|---|---|
| `RobotStudioMCP.AddinMain` | Inside RobotStudio, as a `.rsaddin` | `net10.0-windows` (WinForms) | The actual RobotStudio SDK calls. `AddIn.AddinMain()` is the RobotStudio-invoked entry point: it builds the core tool set, loads optional tool modules, and starts the add-in's pipe server. |
| `RobotStudioMCP.AddinServer` | Inside the add-in process | `net10.0-windows` | Hosts the add-in's tool surface over a plain named pipe (`AddinPipeServer`), plus the `IAddinToolModule`/`RobotStudioMCP.Tools.*.dll` dynamic module loader. Has no SDK calls of its own — it's transport and plugin plumbing that `AddinMain` calls into. Deliberately has no ASP.NET Core / MCP SDK dependency: the add-in runs inside `RobotStudio.exe`, which never declares the ASP.NET Core shared framework, so a `FrameworkReference` to it fails to load at runtime regardless of `EnableDynamicLoading`. |
| `RobotStudioMCP.Core.Station` | Loaded by both `AddinMain` and the Watchdog test project | `netstandard2.0` | Shared data contract (station state shapes) with no RobotStudio SDK dependency. `netstandard2.0` specifically so it can be referenced from both `AddinMain`'s `net10.0-windows` and `RobotStudioMCP.Watchdog.Tests`'s `net10.0` without either project depending on the other's target framework. |
| `RobotStudioMCP.Watchdog` | Standalone process, outside RobotStudio | `net10.0` | stdio MCP server + CLI: RobotStudio process lifecycle (start/stop/kill), `.rspak` package management, health/readiness probing of the add-in. Ships both as a plain exe and as a `dotnet tool` (`robotstudio-watchdog`). |

## Transport choices

- **Add-in (`RobotStudioMCP.AddinServer`):** a plain Windows named pipe (`AddinPipeServer`), named `RobotStudioMCP-Addin-<RobotStudio's own process id>` so multiple RobotStudio instances never collide. This is an internal, same-build protocol between the add-in and the watchdog only — it is never exposed as a real MCP endpoint, so it deliberately skips version negotiation, the initialize/initialized handshake, and notifications entirely (both ends always ship together); it exposes exactly two operations, `list_tools` and `call_tool`. The pipe is created with an explicit ACL granting the local Users group access — .NET's default pipe ACL was observed to block same-user connections across different logon sessions in practice, which otherwise manifests as the client's connect attempt simply never completing. Named pipes aren't network-reachable regardless of this ACL. This replaced an earlier ASP.NET Core/`ModelContextProtocol.AspNetCore`-hosted HTTP transport, which doesn't work: the add-in runs inside `RobotStudio.exe`, which never declares the ASP.NET Core shared framework in its own runtime config, so types from that framework fail to load with `FileNotFoundException` regardless of build settings.
- **Watchdog (`RobotStudioMCP.Watchdog`):** stdio MCP server (the no-argument / `mcp`/`serve` CLI path), because the watchdog is meant to be launched directly by an MCP client (Claude Desktop, etc.) as a child process — stdio is the standard transport for that pattern. The watchdog is the *only* process that speaks real MCP to external clients; it forwards the add-in's own tools (`check_active_station`, `robotstudio_assembly_list`) over the internal pipe and relays the result back as a normal MCP tool result.

## Plugin model

`AddinMain` always registers `CoreTools` (readiness/status: `check_active_station`, `robotstudio_assembly_list`) plus the implicit `get_robotstudio_status` tool from `AddinPipeServer`. Beyond that, `ToolModuleLoader` scans the add-in's own directory for `RobotStudioMCP.Tools.*.dll` files, loads each one with `Assembly.LoadFrom`, and instantiates every public `IAddinToolModule` implementation found inside. Each module contributes a `Name` and a set of `AddinMcpTool`s via `CreateTools()`.

An absent module file is not an error — that's what makes "core edition" possible: this repo ships without any `Tools.*` module, so only the core readiness/status tools are available. A module that's present but fails to load, or that redefines an existing tool name (checked against both the core tools and every module loaded so far), is reported in the module status and contributes no tools; it doesn't take down the add-in.

`RobotStudioMCP.Tools.Station` — a full station-editing tool module (SmartComponents, object manipulation, library catalog, graphic control, etc.) — is excluded from this repository for now. The plugin seam (`IAddinToolModule` + directory-scan loading) is the mechanism that lets such an edition exist as a separate, droppable DLL without the core add-in needing to know about it at compile time.

## Readiness/handshake model

Starting RobotStudio and confirming the add-in is actually usable is a multi-stage handshake owned by the watchdog, not a single call:

1. **Launch** — `StartRobotStudioProcess` starts (or finds an already-tracked) RobotStudio process, optionally with an extra `.rsaddin` (`rsaddinPath`) and a station to open (`stationPath`); both are startup-only and ignored if RobotStudio is already running.
2. **Add-in load classification** — `RobotStudioHealthTools` scans RobotStudio's usage/error logs to classify the target add-in's load state (`loading` / `loaded` / `failed` / `awaiting manual activation` / ...). See `docs/watchdog-addin-load-classification.md` for the full decision table — this is log-evidence based, not a pipe probe, because the add-in's pipe server isn't reachable until RobotStudio has actually finished loading it.
3. **Add-in pipe ping** — once load classification says the add-in should be up, the watchdog connects to the add-in's named pipe and calls `list_tools`, confirming the add-in's own tool server (not just RobotStudio's process) actually responds.

`VerifyAddinMcpReady` runs stages 2–3 together, with per-stage timeouts and retries, and reports which stage is still blocking if it times out. `ready=true` requires both: a non-blocking add-in load classification (see the classification doc for what counts as blocking, including the `HadStartupException` hard-block that overrides an otherwise-successful load) and a successful pipe ping.
