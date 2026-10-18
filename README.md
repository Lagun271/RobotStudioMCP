# RobotStudioMCP

An MCP server and CLI for automating ABB RobotStudio.

- A **watchdog** (`RobotStudioMCP.Watchdog`) that runs as a separate process, manages RobotStudio's lifecycle (start/stop), handles `.rspak` package install/uninstall, and verifies the MCP add-in's readiness — exposed both as an MCP stdio server and as a CLI. For add-in developers: it can also launch RobotStudio with an **add-in you're developing** and track its load state independently, as a dev-loop tool. The watchdog is the only process that speaks MCP to external clients.
- A **core add-in** (`RobotStudioMCP.AddinMain`) that runs inside RobotStudio and exposes station-readiness tools (`check_active_station`, `robotstudio_assembly_list`), with a plugin seam for optional station-editing tool modules. The add-in doesn't speak MCP itself — it exposes its tools over an internal named pipe that the watchdog forwards calls through, since RobotStudio's own process never declares the ASP.NET Core shared framework an in-process MCP/HTTP server would need.

## Prerequisites

- RobotStudio 2026
- .NET 10 SDK
- The `Lagun271.RobotStudio.*` NuGet packages (`Sdk.Ref`, `RspakTool`), published publicly from [`Lagun271/robotstudio-sdk-nuget`](https://github.com/Lagun271/robotstudio-sdk-nuget).

## Install

**Add-in** — build and install the `.rspak` distribution package:

```powershell
dotnet build .\src\RobotStudioMCP.AddinMain\RobotStudioMCP.AddinMain.csproj -c Debug
dotnet msbuild .\src\RobotStudioMCP.AddinMain\RobotStudioMCP.AddinMain.csproj -t:PackRspak -p:Configuration=Debug -p:RspakVersion=0.1.0
```

This produces `RobotStudioMCP-<version>.rspak` — install it via RobotStudio's Package Manager.

**Watchdog** — build it and add the output folder to `PATH`:

```powershell
dotnet build .\src\RobotStudioMCP.Watchdog\RobotStudioMCP.Watchdog.csproj -c Debug
```

Output: `src\RobotStudioMCP.Watchdog\bin\Debug\net10.0\RobotStudioMCP.Watchdog.exe`.

## Quick start

CLI — start RobotStudio and wait until the add-in is ready:

```powershell
RobotStudioMCP.Watchdog.exe start --wait-ready
```

MCP — point an MCP client at the watchdog's stdio server (`RobotStudioMCP.Watchdog.exe mcp`, or no arguments). It exposes lifecycle/readiness/package tools directly, and forwards the add-in's station-readiness tools (`check_active_station`, `robotstudio_assembly_list`) over its internal pipe to the running add-in — there is no separate endpoint to point at for those.

## Developing your own add-in

Launch RobotStudio with an add-in you're building, and track its load state independently of the installed MCP add-in — no need to install it first. Try it with the repo's own test fixture:

```powershell
dotnet build .\test\DummyRobotStudioAddin\DummyRobotStudioAddin.csproj -c Debug
RobotStudioMCP.Watchdog.exe start --rsaddin-path "<repo>\test\DummyRobotStudioAddin\bin\Debug\net10.0-windows\DummyRobotStudioAddin.rsaddin" --wait-ready
```

(Use the path under `bin\Debug\net10.0-windows\`, not the one next to the `.csproj` — see the note below.)

The JSON response's `runtimeReadiness` carries both load states independently: `mcpAddinLoad` for the installed MCP add-in, `rsaddinLoad` for the one you just pointed at.

**Note:** `--rsaddin-path` must point at an `.rsaddin` file with its assembly in the same folder — RobotStudio resolves `<Assembly><FileName>` relative to the manifest's own directory, not the project source tree. For an SDK-style project, that's the `dotnet build` output folder, not the one next to the `.csproj`.

## Learn more

- [`docs/architecture.md`](docs/architecture.md) — component map, transport choices, plugin model, readiness handshake.
- [`docs/mcp-cli-conventions.md`](docs/mcp-cli-conventions.md) — MCP tool naming/result conventions, CLI conventions.

## License

MIT — see [`LICENSE`](LICENSE).
