# RobotStudioMCP.Watchdog

`RobotStudioMCP.Watchdog` is a NuGet-packaged .NET tool for RobotStudio lifecycle, readiness, and package management automation.

## Install

```powershell
dotnet.exe tool install -g RobotStudioMCP.Watchdog
```

To install from a local feed:

```powershell
dotnet.exe tool install -g RobotStudioMCP.Watchdog --add-source .\artifacts\nuget
```

## Command surface

- `robotstudio-watchdog start`
- `robotstudio-watchdog ready`
- `robotstudio-watchdog status`
- `robotstudio-watchdog stop`
- `robotstudio-watchdog station-state`
- `robotstudio-watchdog mcp` (alias `serve`): runs the MCP stdio server explicitly
- no command: also runs the MCP stdio server, kept for backward compatibility

Run `robotstudio-watchdog <command> --help` for a command's full option list, or `--version` for the
build version. Startup arguments such as `--rsaddin-path` and `--station-path` only apply when `start` launches a new RobotStudio process.
If RobotStudio is already running, the watchdog reports `alreadyRunning` instead of trying to inject new CLI arguments into the live process.

`--rsaddin-path` must point at an `.rsaddin` file with its assembly sitting in the **same folder** — RobotStudio
resolves the manifest's `<Assembly><FileName>` relative to the manifest's own directory, not the project source
tree. For an SDK-style project built with `dotnet build`, that means pointing at the copy under
`bin/<Configuration>/<TFM>/`, not the one next to the `.csproj` — the source-tree `.rsaddin` has no `.dll` beside
it, so RobotStudio fails to load it with a `FileNotFoundException` naming the missing assembly.

## Settings

Optional settings can be provided via `robotstudio-watchdog.settings.json` located next to the watchdog executable (same directory as the tool).
Currently supported settings:

- `StartupCooldownAfterKillMs` (integer, milliseconds): time to wait after killing RobotStudio before restarting. Default: `5000`.

Invalid settings cause startup to fail fast with a clear error.

## Notes

- The tool hosts the same watchdog executable project used by the repository.
- It communicates over stdio and is intended for automation and scripting.
- The CLI commands return structured JSON for downstream smoke scripts.
