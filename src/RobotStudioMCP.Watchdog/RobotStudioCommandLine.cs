using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text.Json;

namespace RobotStudioMCP.Watchdog;

internal sealed class RobotStudioCommandLine
{
  private const int JsonSchemaVersion = 1;

  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    WriteIndented = true
  };

  private static readonly string[] HelpAliasArgs = ["--help"];

  private static readonly string WatchdogVersion =
      typeof(RobotStudioCommandLine).Assembly.GetName().Version?.ToString() is { } assemblyVersion
          ? GetFileVersionOrFallback(assemblyVersion)
          : "0.0.0";

  private readonly IRobotStudioProcessStateStore _stateStore;
  private readonly RobotStudioLifecycleTools _lifecycleTools;
  private readonly RobotStudioHealthTools _healthTools;

  public RobotStudioCommandLine(IRobotStudioProcessStateStore stateStore)
  {
    _stateStore = stateStore;
    _lifecycleTools = new RobotStudioLifecycleTools(stateStore);
    _healthTools = new RobotStudioHealthTools(stateStore);
  }

  /// <summary>
  /// Parses and runs <paramref name="args"/> as a CLI invocation. Returns <see langword="false"/> only
  /// for a truly empty argument list, so the caller can fall back to running the stdio MCP server (the
  /// `mcp`/`serve` command is the discoverable equivalent of that same fallback). Any non-empty input,
  /// including a command System.CommandLine doesn't recognize, is fully handled here -- it no longer
  /// falls through to starting the server, which is what let a typo like `strat` silently hang as a
  /// stdio server with no input ever coming.
  /// </summary>
  public async Task<bool> TryRunAsync(string[] args)
  {
    if (args.Length == 0)
    {
      return false;
    }

    // "help" (a bare word) is kept as an alias for "--help" for backward compatibility; System.CommandLine
    // only recognizes the latter as a root-level option.
    var effectiveArgs = args is [{ } first, .. var rest] && string.Equals(first, "help", StringComparison.OrdinalIgnoreCase)
        ? HelpAliasArgs.Concat(rest).ToArray()
        : args;

    var exitCode = await BuildRootCommand().Parse(effectiveArgs).InvokeAsync();
    Environment.ExitCode = exitCode;
    return true;
  }

  private RootCommand BuildRootCommand()
  {
    var ready = new ReadyOptions();

    var robotStudioExePathOption = new Option<string?>("--robotstudio-exe-path")
    { Description = "Full path to RobotStudio.exe. If omitted, defaults to Program Files (x86) path, then PATH lookup." };
    var stationPathOption = new Option<string?>("--station-path")
    { Description = "Optional absolute full path to a RobotStudio station/project file to open during launch." };
    var envOption = new Option<string[]>("--env", "--environment")
    { Description = "KEY=VALUE process-scoped environment variable for the launched process. Repeatable." };
    var killAllRobotStudioOption = new Option<bool>("--kill-all-robotstudio")
    { Description = "If set, kill all RobotStudio processes before launch." };
    var killAllRobVCOption = new Option<bool>("--kill-all-robvc")
    { Description = "If set, kill all RobVC processes before launch." };
    var startTimeoutSecondsOption = new Option<int>("--start-timeout-seconds")
    { Description = "Maximum seconds to wait for startup confirmation.", DefaultValueFactory = _ => 30 };
    var waitReadyOption = new Option<bool>("--wait-ready")
    { Description = "After a successful launch, also wait for add-in readiness (see the `ready` command's options)." };

    var startCommand = new Command("start", "Start RobotStudio and optionally wait for readiness.")
    {
      robotStudioExePathOption, ready.RsaddinPath, stationPathOption, envOption,
      killAllRobotStudioOption, killAllRobVCOption, startTimeoutSecondsOption, waitReadyOption,
      ready.PipeName, ready.TimeoutSeconds, ready.RetryDelayMs, ready.RequireInstalled, ready.PackageId,
      ready.UsageLogsRoot, ready.ErrorLogsRoot, ready.DistributionPackagesRoot, ready.DistributionPackagesRoot2,
      ready.ProbeTimeoutMs, ready.RequireAddinLoaded
    };
    startCommand.SetAction(async (parseResult, cancellationToken) =>
    {
      var environment = ParseEnvironment(parseResult.GetValue(envOption) ?? []);
      var waitReady = parseResult.GetValue(waitReadyOption);

      var start = await _lifecycleTools.StartRobotStudioProcess(
          robotStudioExePath: parseResult.GetValue(robotStudioExePathOption),
          rsaddinPath: parseResult.GetValue(ready.RsaddinPath),
          stationPath: parseResult.GetValue(stationPathOption),
          environment: environment.Count == 0 ? null : environment,
          killAllRobotStudio: parseResult.GetValue(killAllRobotStudioOption),
          killAllRobVC: parseResult.GetValue(killAllRobVCOption),
          timeoutSeconds: parseResult.GetValue(startTimeoutSecondsOption),
          cancellationToken: cancellationToken);

      object? readyResult = null;
      var ok = GetBoolProperty(start, "started") == true;

      if (ok && waitReady)
      {
        readyResult = await ready.InvokeAsync(_healthTools, parseResult, cancellationToken);
        ok = GetBoolProperty(readyResult, "ready") == true;
      }

      WriteJson(new { schemaVersion = JsonSchemaVersion, command = "start", ok, start, ready = readyResult, waitReady });
      return ok ? 0 : 1;
    });

    var readyCommand = new Command("ready", "Check readiness for the installed MCP add-in and any optional user add-in from structured diagnostics.")
    {
      ready.PipeName, ready.TimeoutSeconds, ready.RetryDelayMs, ready.RequireInstalled, ready.PackageId,
      ready.UsageLogsRoot, ready.ErrorLogsRoot, ready.DistributionPackagesRoot, ready.DistributionPackagesRoot2,
      ready.RsaddinPath, ready.ProbeTimeoutMs, ready.RequireAddinLoaded
    };
    readyCommand.SetAction(async (parseResult, cancellationToken) =>
    {
      var readyResult = await ready.InvokeAsync(_healthTools, parseResult, cancellationToken);
      var ok = GetBoolProperty(readyResult, "ready") == true;
      WriteJson(new { schemaVersion = JsonSchemaVersion, command = "ready", ok, ready = readyResult });
      return ok ? 0 : 1;
    });

    var statusCommand = new Command("status", "Report tracked RobotStudio process state.");
    statusCommand.SetAction(parseResult =>
    {
      var status = _healthTools.GetRobotStudioProcessStatus();
      WriteJson(new { schemaVersion = JsonSchemaVersion, command = "status", ok = true, status, trackedProcess = _stateStore.Current });
      return 0;
    });

    var gracefulTimeoutSecondsOption = new Option<int>("--graceful-timeout-seconds")
    { Description = "Seconds to wait for graceful shutdown before force kill.", DefaultValueFactory = _ => 10 };
    var forceKillOnlyOption = new Option<bool>("--force-kill-only")
    { Description = "If set, skip graceful close and force-kill immediately to avoid save dialogs." };

    var stopCommand = new Command("stop", "Stop the RobotStudio process previously started/tracked by the watchdog.")
    {
      gracefulTimeoutSecondsOption, forceKillOnlyOption
    };
    stopCommand.SetAction(async (parseResult, cancellationToken) =>
    {
      var stop = await _lifecycleTools.StopStartedRobotStudioProcess(
          gracefulTimeoutSeconds: parseResult.GetValue(gracefulTimeoutSecondsOption),
          forceKillOnly: parseResult.GetValue(forceKillOnlyOption),
          cancellationToken: cancellationToken);

      var ok = GetBoolProperty(stop, "stopped") == true || GetBoolProperty(stop, "nothingToStop") == true;
      WriteJson(new { schemaVersion = JsonSchemaVersion, command = "stop", ok, stop });
      return ok ? 0 : 1;
    });

    var stationStateCommand = new Command("station-state", "Check if a RobotStudio station is active/open, via the add-in's check_active_station tool.")
    {
      ready.PipeName, ready.ProbeTimeoutMs
    };
    stationStateCommand.SetAction(async parseResult =>
    {
      var stationState = await _healthTools.GetActiveStationStatus(
          pipeName: parseResult.GetValue(ready.PipeName),
          probeTimeoutMs: parseResult.GetValue(ready.ProbeTimeoutMs));

      var ok = GetBoolProperty(stationState, "ok") == true;
      WriteJson(new { schemaVersion = JsonSchemaVersion, command = "station-state", ok, stationState });
      return ok ? 0 : 1;
    });

    var mcpCommand = new Command("mcp", "Run the MCP stdio server. Equivalent to passing no command at all (kept for compatibility); use this form when you want the server explicitly, for example from a script.");
    mcpCommand.Aliases.Add("serve");
    mcpCommand.SetAction(async (_, cancellationToken) =>
    {
      await McpStdioServerHost.RunAsync(_stateStore, []);
      return 0;
    });

    var root = new RootCommand("RobotStudioMCP.Watchdog command surface")
    {
      startCommand, readyCommand, statusCommand, stopCommand, stationStateCommand, mcpCommand
    };

    ReplaceVersionAction(root);
    return root;
  }

  private static void ReplaceVersionAction(RootCommand root)
  {
    // The default --version reads AssemblyInformationalVersionAttribute, which this repo doesn't emit
    // (see Directory.Build.props); report the GitVersion-stamped file version instead, consistent with
    // how the add-in reports its own version.
    var defaultVersionOption = root.Options.FirstOrDefault(o => o.Name == "--version");
    if (defaultVersionOption is not null)
    {
      root.Options.Remove(defaultVersionOption);
    }

    root.Add(new Option<bool>("--version") { Description = "Show version information.", Action = new VersionAction() });
  }

  private sealed class VersionAction : SynchronousCommandLineAction
  {
    public override int Invoke(ParseResult parseResult)
    {
      parseResult.InvocationConfiguration.Output.WriteLine(WatchdogVersion);
      return 0;
    }
  }

  /// <summary>
  /// The full option set behind <c>VerifyAddinMcpReady</c>, defined once and shared by the `ready`
  /// command and `start --wait-ready` instead of being hand-mapped twice (the drift risk the protocol
  /// review called out -- each option here appears in the option list of only the command(s) that use
  /// it, but the name/default/mapping to the tool parameter is written exactly once).
  /// </summary>
  private sealed class ReadyOptions
  {
    public Option<string?> PipeName { get; } = new("--pipe-name")
    { Description = "Optional add-in pipe name override. Default: derived from the tracked RobotStudio process id." };

    public Option<int> TimeoutSeconds { get; } = new("--timeout-seconds")
    { Description = "Total timeout seconds for the readiness retry loop.", DefaultValueFactory = _ => 30 };

    public Option<int> RetryDelayMs { get; } = new("--retry-delay-ms")
    { Description = "Delay between retries in milliseconds.", DefaultValueFactory = _ => 500 };

    public Option<bool> RequireInstalled { get; } = new("--require-installed")
    { Description = "Require package-path verification to pass before runtime readiness is considered ready." };

    public Option<string> PackageId { get; } = new("--package-id")
    { Description = "Expected installed package ID/prefix for package-path verification.", DefaultValueFactory = _ => "RobotStudioMCP" };

    public Option<string?> UsageLogsRoot { get; } = new("--usage-logs-root") { Description = "Optional override for UsageLogs root." };

    public Option<string?> ErrorLogsRoot { get; } = new("--error-logs-root") { Description = "Optional override for ErrorLogs root." };

    public Option<string?> DistributionPackagesRoot { get; } = new("--distribution-packages-root")
    { Description = "Optional override for primary distribution package root." };

    public Option<string?> DistributionPackagesRoot2 { get; } = new("--distribution-packages-root2")
    { Description = "Optional override for secondary distribution package root." };

    public Option<string?> RsaddinPath { get; } = new("--rsaddin-path")
    { Description = "Optional absolute full path to an additional `.rsaddin` file to load via /addin alongside the installed MCP add-in. Its assembly must sit in the same folder (e.g. the build output dir, not the project source tree)." };

    public Option<int> ProbeTimeoutMs { get; } = new("--probe-timeout-ms")
    { Description = "Per-probe timeout in milliseconds for the add-in pipe ping.", DefaultValueFactory = _ => 2000 };

    public Option<bool> RequireAddinLoaded { get; } = new("--require-addin-loaded")
    { Description = "Require AddinLoaded log evidence and no load-failure evidence before ready=true." };

    public Task<object> InvokeAsync(RobotStudioHealthTools healthTools, ParseResult parseResult, CancellationToken cancellationToken) =>
        healthTools.VerifyAddinMcpReady(
            pipeName: parseResult.GetValue(PipeName),
            timeoutSeconds: parseResult.GetValue(TimeoutSeconds),
            retryDelayMs: parseResult.GetValue(RetryDelayMs),
            requireInstalled: parseResult.GetValue(RequireInstalled),
            packageId: parseResult.GetValue(PackageId)!,
            usageLogsRoot: parseResult.GetValue(UsageLogsRoot),
            errorLogsRoot: parseResult.GetValue(ErrorLogsRoot),
            distributionPackagesRoot: parseResult.GetValue(DistributionPackagesRoot),
            distributionPackagesRoot2: parseResult.GetValue(DistributionPackagesRoot2),
            rsaddinPath: parseResult.GetValue(RsaddinPath),
            probeTimeoutMs: parseResult.GetValue(ProbeTimeoutMs),
            requireAddinLoaded: parseResult.GetValue(RequireAddinLoaded),
            cancellationToken: cancellationToken);
  }

  private static Dictionary<string, string> ParseEnvironment(IEnumerable<string> rawPairs)
  {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in rawPairs)
    {
      var pair = raw.Split('=', 2, StringSplitOptions.TrimEntries);
      if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]))
      {
        continue;
      }

      result[pair[0]] = pair[1];
    }

    return result;
  }

  private static bool? GetBoolProperty(object? payload, string propertyName)
  {
    if (payload is null)
    {
      return null;
    }

    using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, JsonOptions));
    if (!document.RootElement.TryGetProperty(propertyName, out var property))
    {
      return null;
    }

    return property.ValueKind switch
    {
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => null
    };
  }

  private static void WriteJson(object payload)
  {
    Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
  }

  // GitVersion stamps $(FileVersion)/$(InformationalVersion) into the native Win32 version resource at
  // build time regardless of the repo-wide AssemblyInfo-attribute generation settings (see
  // Directory.Build.props); reading it back this way mirrors AddInMain.GetServerVersion.
  private static string GetFileVersionOrFallback(string assemblyVersionFallback)
  {
    try
    {
      var location = typeof(RobotStudioCommandLine).Assembly.Location;
      if (!string.IsNullOrWhiteSpace(location))
      {
        var info = FileVersionInfo.GetVersionInfo(location);
        if (!string.IsNullOrWhiteSpace(info.ProductVersion))
        {
          return info.ProductVersion;
        }
      }
    }
    catch
    {
      // Fall through to the assembly version fallback.
    }

    return assemblyVersionFallback;
  }
}
