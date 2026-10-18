using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RobotStudioMCP.Watchdog;

/// <summary>
/// Builds and runs the watchdog's stdio MCP server. Shared by the implicit no-command entry point
/// (Program.cs, kept for compatibility) and the explicit `mcp`/`serve` CLI command.
/// </summary>
internal static class McpStdioServerHost
{
  public static async Task RunAsync(IRobotStudioProcessStateStore stateStore, string[] args)
  {
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();

    builder.Services.AddSingleton(stateStore);

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            ConventionalToolResultFilter.ApplyConventionalIsError(await next(context, cancellationToken))));

    await builder.Build().RunAsync();
  }
}
