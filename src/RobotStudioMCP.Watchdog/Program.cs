using RobotStudioMCP.Watchdog;

RobotStudioWatchdogSettings.LoadFromDefaultLocations();

var stateStore = new RobotStudioProcessStateStore();
if (await new RobotStudioCommandLine(stateStore).TryRunAsync(args))
{
  return;
}

// No command given: run the stdio MCP server directly (kept for compatibility -- see the explicit
// `mcp`/`serve` command for the discoverable equivalent).
await McpStdioServerHost.RunAsync(stateStore, args);
