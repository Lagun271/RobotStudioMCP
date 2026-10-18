using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RobotStudioMCP.Watchdog;

internal sealed class PackagePathDiagnostics
{
  public bool Installed { get; init; }
  public string Method { get; init; } = "rspak";
  public string PackageId { get; init; } = "RobotStudioMCP";
  public IReadOnlyList<string> SearchedRoots { get; init; } = Array.Empty<string>();
  public IReadOnlyList<string> MatchedPackages { get; init; } = Array.Empty<string>();
  public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
  public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

internal sealed class RobotStudioLogFileInfo
{
  public string Path { get; init; } = string.Empty;
  public DateTime LastWriteTimeUtc { get; init; }
  public long LengthBytes { get; init; }
}

internal sealed class RobotStudioLogDiscovery
{
  public string Kind { get; init; } = string.Empty;
  public string Root { get; init; } = string.Empty;
  public IReadOnlyList<RobotStudioLogFileInfo> Files { get; init; } = Array.Empty<RobotStudioLogFileInfo>();
  public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

internal sealed class RobotStudioLicenseDiagnostics
{
  public bool Found { get; init; }
  public string? LogPath { get; init; }
  public string? SessionStartUtc { get; init; }
  public string? AppVersion { get; init; }
  public string? LicenseLevel { get; init; }
  public string? Licenses { get; init; }
  public string? LicenseFeatures { get; init; }
  public string? PremiumLicenseType { get; init; }
  public string? Error { get; init; }
}

internal sealed class RobotStudioAddinLoadLogEvidence
{
  public string PackageId { get; init; } = string.Empty;
  public IReadOnlyList<string> LoadProgressEntries { get; init; } = Array.Empty<string>();
  public IReadOnlyList<string> LoadFailureEntries { get; init; } = Array.Empty<string>();
  public IReadOnlyList<string> IgnoredFailureEntries { get; init; } = Array.Empty<string>();
  public string? FirstFailureLine { get; init; }
  public string? FirstFailureEvidence { get; init; }
}

internal sealed class RobotStudioAddinLoadClassification
{
  public string Code { get; init; } = "ADDIN_LOAD_UNKNOWN";
  public string Status { get; init; } = "unknown";
  public bool Ok { get; init; }
  public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
  public string Hint { get; init; } = string.Empty;
  public string? FirstAddinEntrypointException { get; init; }
  public string? StartupFailureReason { get; init; }
  public bool HadStartupException { get; init; }
}

internal sealed class RobotStudioAddinLoadState
{
  public RobotStudioAddinLoadLogEvidence McpEvidence { get; init; } = new();
  public RobotStudioAddinLoadClassification McpLoad { get; init; } = new();
  public RobotStudioAddinLoadClassification RsaddinLoad { get; init; } = new();
  public string? RsaddinId { get; init; }
}

internal enum VerifyStage
{
  Passed,
  Runtime,
  Health,
  McpInitialize,
  AddinLoad,
  License
}

internal enum VerifyFailureType
{
  None,
  HealthProbe,
  McpInitialize,
  RobotStudioLicense,
  RobotStudioAddinLoad
}

[McpServerToolType]
public sealed class RobotStudioHealthTools
{
  internal const string DefaultUsageLogsRoot = @"C:\ProgramData\ABB\RobotStudio\UsageLogs";
  internal const string DefaultErrorLogsRoot = @"C:\ProgramData\ABB\RobotStudio\ErrorLogs";
  private static readonly char[] LicenseFeatureSeparators = { '|', ';' };

  private static readonly string WatchdogVersion =
      typeof(RobotStudioHealthTools).Assembly.GetName().Version?.ToString() ?? "0.0.0";

  private static string? DefaultUserDistributionPackagesRoot
  {
    get
    {
      var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "ABB", "DistributionPackages");
    }
  }

  private static string? DefaultUserDistributionPackagesRoot2
  {
    get
    {
      var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "ABB", "DistributionPackages2");
    }
  }

  private readonly IRobotStudioProcessStateStore _stateStore;

  public RobotStudioHealthTools(IRobotStudioProcessStateStore stateStore)
  {
    _stateStore = stateStore;
  }

  [McpServerTool(ReadOnly = true, Title = "RobotStudio process status"), Description("Check if RobotStudio process is running on this machine.")]
  public object GetRobotStudioProcessStatus()
  {
    var processes = Process.GetProcessesByName("RobotStudio");
    var ids = processes.Select(p => p.Id).ToArray();
    var tracked = _stateStore.Current;

    return new
    {
      isRunning = ids.Length > 0,
      count = ids.Length,
      processIds = ids,
      trackedProcess = tracked
    };
  }

  [McpServerTool(ReadOnly = true), Description("Check whether the RobotStudio add-in's internal pipe is reachable.")]
  public async Task<object> CheckAddinEndpoint(
      [Description("Optional pipe name override. Default: derived from the tracked RobotStudio process id.")] string? pipeName = null)
  {
    if (!TryResolveAddinPipeName(pipeName, out var resolvedPipeName, out var pipeNameError))
    {
      return new { reachable = false, error = pipeNameError };
    }

    var (ok, toolCount, serverName, serverVersion, error) = await PingAddinPipe(resolvedPipeName, probeTimeoutMs: 2000);
    return new { reachable = ok, pipeName = resolvedPipeName, toolCount, serverName, serverVersion, error };
  }

  [McpServerTool(ReadOnly = true), Description("Checks if a RobotStudio station is active/open, via the add-in's check_active_station tool.")]
  public async Task<object> GetActiveStationStatus(
      [Description("Optional pipe name override. Default: derived from the tracked RobotStudio process id.")] string? pipeName = null,
      [Description("Probe timeout in milliseconds.")] int probeTimeoutMs = 2000)
  {
    if (!TryResolveAddinPipeName(pipeName, out var resolvedPipeName, out var pipeNameError))
    {
      return new { ok = false, error = pipeNameError };
    }

    return await CallAddinTool(resolvedPipeName, "check_active_station", probeTimeoutMs);
  }

  // Mirrors RobotStudioMCP.AddIn.PipeNameForProcess in RobotStudioMCP.AddinMain exactly; duplicated
  // rather than shared via a project reference because the watchdog has no other reason to depend on
  // the add-in project (which pulls in the RobotStudio SDK ref assemblies). If this format ever
  // changes, it has to change in both places.
  private static string PipeNameForProcess(int processId) => $"RobotStudioMCP-Addin-{processId}";

  private bool TryResolveAddinPipeName(string? pipeNameOverride, out string pipeName, out string? error)
  {
    pipeName = string.Empty;
    error = null;

    if (!string.IsNullOrWhiteSpace(pipeNameOverride))
    {
      pipeName = pipeNameOverride;
      return true;
    }

    var trackedProcessId = _stateStore.Current.ProcessId;
    if (trackedProcessId is null)
    {
      error = "No tracked RobotStudio process; start one first or pass an explicit pipeName.";
      return false;
    }

    pipeName = PipeNameForProcess(trackedProcessId.Value);
    return true;
  }

  [McpServerTool(ReadOnly = true), Description("Verify installed add-in package presence using RobotStudio distribution package roots.")]
  public static object VerifyAddinInstallation(
      [Description("Expected package ID/prefix. Default: RobotStudioMCP")] string packageId = "RobotStudioMCP",
      [Description("Optional override for primary distribution package root.")] string? distributionPackagesRoot = null,
      [Description("Optional override for secondary distribution package root.")] string? distributionPackagesRoot2 = null)
  {
    return ToInstallationPayload(ScanPackagePaths(packageId, distributionPackagesRoot, distributionPackagesRoot2));
  }

  [McpServerTool(ReadOnly = true), Description("Discover recent RobotStudio usage/error log files in ProgramData locations.")]
  public static object DiscoverRobotStudioLogs(
      [Description("Log kind: usage, error, or all.")] string kind = "all",
      [Description("Maximum files to return per log kind.")] int maxFilesPerKind = 5,
      [Description("Optional override for UsageLogs root.")] string? usageLogsRoot = null,
      [Description("Optional override for ErrorLogs root.")] string? errorLogsRoot = null)
  {
    maxFilesPerKind = Math.Clamp(maxFilesPerKind, 1, 50);
    var normalizedKind = NormalizeLogKind(kind);

    var usage = normalizedKind is "usage" or "all"
        ? DiscoverLogs("usage", usageLogsRoot, maxFilesPerKind)
        : null;
    var error = normalizedKind is "error" or "all"
        ? DiscoverLogs("error", errorLogsRoot, maxFilesPerKind)
        : null;

    return new
    {
      kind = normalizedKind,
      usageLogs = usage is null ? null : ToLogDiscoveryPayload(usage),
      errorLogs = error is null ? null : ToLogDiscoveryPayload(error)
    };
  }

  [McpServerTool(ReadOnly = true), Description("Read the tail of a RobotStudio .rslog file for troubleshooting.")]
  public static object ReadRobotStudioLogTail(
      [Description("Full path to a RobotStudio .rslog file, under the UsageLogs or ErrorLogs root.")] string logPath,
      [Description("Maximum number of lines to return from the end of the file.")] int tailLines = 100,
      [Description("Maximum total characters to return.")] int maxChars = 12000,
      [Description("Optional override for UsageLogs root (must match the root 'logPath' was discovered under).")] string? usageLogsRoot = null,
      [Description("Optional override for ErrorLogs root (must match the root 'logPath' was discovered under).")] string? errorLogsRoot = null)
  {
    var normalizedPath = (logPath ?? string.Empty).Trim();
    tailLines = Math.Clamp(tailLines, 1, 1000);
    maxChars = Math.Clamp(maxChars, 200, 50000);

    if (string.IsNullOrWhiteSpace(normalizedPath))
    {
      return new
      {
        ok = false,
        error = "The 'logPath' argument is required."
      };
    }

    if (!TryResolveKnownLogPath(normalizedPath, usageLogsRoot, errorLogsRoot, out var resolvedPath, out var pathError))
    {
      return new
      {
        ok = false,
        logPath = normalizedPath,
        error = pathError
      };
    }

    if (!File.Exists(resolvedPath))
    {
      return new
      {
        ok = false,
        logPath = resolvedPath,
        error = "The log file was not found."
      };
    }

    try
    {
      var allLines = ReadAllLinesShared(resolvedPath).ToArray();
      var selected = allLines.Skip(Math.Max(0, allLines.Length - tailLines)).ToArray();
      var fullContent = string.Join(Environment.NewLine, selected);
      var content = fullContent;
      if (content.Length > maxChars)
      {
        content = content[^maxChars..];
      }

      return new
      {
        ok = true,
        logPath = resolvedPath,
        lineCount = allLines.Length,
        returnedTailLines = selected.Length,
        truncated = content.Length < fullContent.Length,
        content
      };
    }
    catch (Exception ex)
    {
      return new
      {
        ok = false,
        logPath = normalizedPath,
        error = ex.Message
      };
    }
  }

  // Known roots .rslog paths must resolve under; mirrors the defaults DiscoverRobotStudioLogs uses so a
  // path it returns is always acceptable here, while blocking reads of arbitrary files elsewhere on disk.
  private static bool TryResolveKnownLogPath(string logPath, string? usageLogsRoot, string? errorLogsRoot, out string resolvedPath, out string? error)
  {
    resolvedPath = string.Empty;
    error = null;

    if (!string.Equals(Path.GetExtension(logPath), ".rslog", StringComparison.OrdinalIgnoreCase))
    {
      error = "Only '.rslog' files can be read.";
      return false;
    }

    string fullPath;
    try
    {
      fullPath = Path.GetFullPath(logPath);
    }
    catch (Exception ex)
    {
      error = $"Invalid path: {ex.Message}";
      return false;
    }

    var roots = new[]
    {
      Path.GetFullPath(string.IsNullOrWhiteSpace(usageLogsRoot) ? DefaultUsageLogsRoot : usageLogsRoot),
      Path.GetFullPath(string.IsNullOrWhiteSpace(errorLogsRoot) ? DefaultErrorLogsRoot : errorLogsRoot)
    };

    var underKnownRoot = roots.Any(root =>
        fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && (fullPath.Length == root.Length
            || fullPath[root.Length] == Path.DirectorySeparatorChar
            || fullPath[root.Length] == Path.AltDirectorySeparatorChar));

    if (!underKnownRoot)
    {
      error = "The 'logPath' must be under the UsageLogs or ErrorLogs root.";
      return false;
    }

    resolvedPath = fullPath;
    return true;
  }

  [McpServerTool(ReadOnly = true), Description("Verify the installed MCP add-in is ready and, when requested, classify an optional rsaddin by package-path info, RobotStudio logs, and the add-in's internal pipe, with retries.")]
  public async Task<object> VerifyAddinMcpReady(
      [Description("Optional pipe name override. Default: derived from the tracked RobotStudio process id.")] string? pipeName = null,
      [Description("Total timeout seconds for retry loop.")] int timeoutSeconds = 30,
      [Description("Delay between retries in milliseconds.")] int retryDelayMs = 500,
      [Description("Require package-path verification to pass before runtime readiness is considered ready.")] bool requireInstalled = false,
      [Description("Expected installed package ID/prefix for package-path verification.")] string packageId = "RobotStudioMCP",
      [Description("Optional override for UsageLogs root.")] string? usageLogsRoot = null,
      [Description("Optional override for ErrorLogs root.")] string? errorLogsRoot = null,
      [Description("Optional override for primary distribution package root.")] string? distributionPackagesRoot = null,
      [Description("Optional override for secondary distribution package root.")] string? distributionPackagesRoot2 = null,
      [Description("Optional rsaddin path used for launch; used only for rsaddin log classification.")] string? rsaddinPath = null,
      [Description("Per-probe timeout in milliseconds for the add-in pipe ping.")] int probeTimeoutMs = 2000,
      [Description("Require AddinLoaded log evidence and no load-failure evidence before ready=true.")] bool requireAddinLoaded = false,
      IProgress<ProgressNotificationValue>? progress = null,
      CancellationToken cancellationToken = default)
  {
    timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 300);
    retryDelayMs = Math.Clamp(retryDelayMs, 100, 5000);
    probeTimeoutMs = Math.Clamp(probeTimeoutMs, 100, 10000);

    if (!TryResolveAddinPipeName(pipeName, out var resolvedPipeName, out var pipeNameError))
    {
      return new { ready = false, pipeName, error = pipeNameError };
    }

    var processStatus = GetRobotStudioProcessStatus();
    var processJson = JsonSerializer.SerializeToElement(processStatus);
    var trackedProcess = processJson.GetProperty("trackedProcess");

    var packagePaths = ScanPackagePaths(packageId, distributionPackagesRoot, distributionPackagesRoot2);
    var packagePathsPayload = ToPackagePathsPayload(packagePaths);
    var packagePathsJson = JsonSerializer.SerializeToElement(packagePathsPayload);
    var packagePathsOk = packagePathsJson.TryGetProperty("installed", out var installedProp) && installedProp.GetBoolean();

    var trackedStartUtc = trackedProcess.TryGetProperty("ProcessStartTimeUtc", out var trackedStartProp)
        && trackedStartProp.ValueKind == JsonValueKind.String
        && DateTime.TryParse(trackedStartProp.GetString(), out var parsedTrackedStartUtc)
        ? parsedTrackedStartUtc.ToUniversalTime()
        : (DateTime?)null;

    var licenseDiagnostics = ParseLatestUsageLogLicenseInfo(usageLogsRoot);
    var licenseReadiness = EvaluateLicenseReadinessForAddinLoad(licenseDiagnostics);
    var addinLoadState = GetAddinLoadState(packageId, rsaddinPath, usageLogsRoot, errorLogsRoot, trackedStartUtc, licenseDiagnostics);
    var mcpAddinLogEvidence = addinLoadState.McpEvidence;
    var mcpAddinLoad = addinLoadState.McpLoad;
    var rsaddinLoad = addinLoadState.RsaddinLoad;
    var rsaddinId = addinLoadState.RsaddinId;

    var startedAtUtc = DateTime.UtcNow;
    var deadline = startedAtUtc.AddSeconds(timeoutSeconds);
    var attempts = 0;
    var timedOut = false;
    string? lastPingError = null;
    var addinReachable = false;
    string? serverName = null;
    string? serverVersion = null;
    var failingStage = VerifyStage.Runtime;
    var lastFailureType = VerifyFailureType.None;
    string? lastFailureMessage = null;
    DateTime? lastProbeAtUtc = null;
    var licenseBlocksAddinLoad = licenseDiagnostics.Found
        && string.Equals(licenseDiagnostics.LicenseLevel?.Trim(), "Free", StringComparison.OrdinalIgnoreCase);
    var immediateAddinLoadFailure = IsDefinitiveAddinLoadFailure(mcpAddinLoad) || IsDefinitiveAddinLoadFailure(rsaddinLoad);

    if (licenseBlocksAddinLoad)
    {
      failingStage = VerifyStage.License;
      lastFailureType = VerifyFailureType.RobotStudioLicense;
      lastFailureMessage = licenseReadiness.reason;
      lastProbeAtUtc = DateTime.UtcNow;
    }
    else if (immediateAddinLoadFailure)
    {
      var isLicenseFeatureMissing = rsaddinLoad.Code == "ADDIN_LOAD_LICENSE_FEATURE_MISSING";
      failingStage = isLicenseFeatureMissing ? VerifyStage.License : VerifyStage.AddinLoad;
      lastFailureType = isLicenseFeatureMissing ? VerifyFailureType.RobotStudioLicense : VerifyFailureType.RobotStudioAddinLoad;
      lastFailureMessage = IsDefinitiveAddinLoadFailure(mcpAddinLoad)
          ? (mcpAddinLoad.Evidence.Count > 0 ? mcpAddinLoad.Evidence[0] : mcpAddinLoad.Hint)
          : (rsaddinLoad.Evidence.Count > 0 ? rsaddinLoad.Evidence[0] : rsaddinLoad.Hint);
      lastProbeAtUtc = DateTime.UtcNow;
    }

    if (!licenseBlocksAddinLoad && !immediateAddinLoadFailure)
    {
      while (DateTime.UtcNow <= deadline)
      {
        attempts++;
        lastProbeAtUtc = DateTime.UtcNow;
        failingStage = VerifyStage.Health;

        if (!licenseDiagnostics.Found)
        {
          licenseDiagnostics = ParseLatestUsageLogLicenseInfo(usageLogsRoot);
          licenseReadiness = EvaluateLicenseReadinessForAddinLoad(licenseDiagnostics);
        }

        progress?.Report(new ProgressNotificationValue
        {
          Progress = Math.Clamp((float)((DateTime.UtcNow - startedAtUtc).TotalSeconds / timeoutSeconds), 0f, 1f),
          Total = 1f,
          Message = $"Probing add-in pipe (attempt {attempts})."
        });

        var pingResult = await PingAddinPipe(resolvedPipeName, probeTimeoutMs, cancellationToken);
        if (pingResult.ok)
        {
          addinReachable = true;
          serverName = pingResult.serverName;
          serverVersion = pingResult.serverVersion;
          failingStage = VerifyStage.Passed;
          break;
        }

        lastPingError = pingResult.error;
        lastFailureType = VerifyFailureType.HealthProbe;
        lastFailureMessage = lastPingError;

        addinLoadState = GetAddinLoadState(packageId, rsaddinPath, usageLogsRoot, errorLogsRoot, trackedStartUtc, licenseDiagnostics);
        mcpAddinLogEvidence = addinLoadState.McpEvidence;
        mcpAddinLoad = addinLoadState.McpLoad;
        rsaddinLoad = addinLoadState.RsaddinLoad;
        rsaddinId = addinLoadState.RsaddinId;

        if (IsDefinitiveAddinLoadFailure(mcpAddinLoad) || IsDefinitiveAddinLoadFailure(rsaddinLoad))
        {
          var isLicenseFeatureMissing = rsaddinLoad.Code == "ADDIN_LOAD_LICENSE_FEATURE_MISSING";
          failingStage = isLicenseFeatureMissing ? VerifyStage.License : VerifyStage.AddinLoad;
          lastFailureType = isLicenseFeatureMissing ? VerifyFailureType.RobotStudioLicense : VerifyFailureType.RobotStudioAddinLoad;
          lastFailureMessage = IsDefinitiveAddinLoadFailure(mcpAddinLoad)
              ? (mcpAddinLoad.Evidence.Count > 0 ? mcpAddinLoad.Evidence[0] : mcpAddinLoad.Hint)
              : (rsaddinLoad.Evidence.Count > 0 ? rsaddinLoad.Evidence[0] : rsaddinLoad.Hint);
          break;
        }

        if (DateTime.UtcNow > deadline)
        {
          timedOut = true;
          break;
        }

        await Task.Delay(retryDelayMs, cancellationToken);
      }
    }

    if (addinReachable)
    {
      while (DateTime.UtcNow <= deadline)
      {
        if (!licenseDiagnostics.Found)
        {
          licenseDiagnostics = ParseLatestUsageLogLicenseInfo(usageLogsRoot);
          licenseReadiness = EvaluateLicenseReadinessForAddinLoad(licenseDiagnostics);
        }

        addinLoadState = GetAddinLoadState(packageId, rsaddinPath, usageLogsRoot, errorLogsRoot, trackedStartUtc, licenseDiagnostics);
        mcpAddinLogEvidence = addinLoadState.McpEvidence;
        mcpAddinLoad = addinLoadState.McpLoad;
        rsaddinLoad = addinLoadState.RsaddinLoad;
        rsaddinId = addinLoadState.RsaddinId;

        var hasMcpAddinFailure = IsDefinitiveAddinLoadFailure(mcpAddinLoad);
        var hasRsaddinFailure = IsDefinitiveAddinLoadFailure(rsaddinLoad);
        var needsMcpAddinEvidence = requireAddinLoaded && !mcpAddinLoad.Ok;
        var needsRsaddinEvidence = !string.IsNullOrWhiteSpace(rsaddinId) && rsaddinLoad.Code == "ADDIN_LOAD_UNKNOWN";

        if (hasMcpAddinFailure || hasRsaddinFailure)
        {
          var isLicenseFeatureMissing = rsaddinLoad.Code == "ADDIN_LOAD_LICENSE_FEATURE_MISSING";
          failingStage = isLicenseFeatureMissing ? VerifyStage.License : VerifyStage.AddinLoad;
          lastFailureType = isLicenseFeatureMissing ? VerifyFailureType.RobotStudioLicense : VerifyFailureType.RobotStudioAddinLoad;
          lastFailureMessage = hasMcpAddinFailure
              ? (mcpAddinLoad.Evidence.Count > 0 ? mcpAddinLoad.Evidence[0] : mcpAddinLoad.Hint)
              : (rsaddinLoad.Evidence.Count > 0 ? rsaddinLoad.Evidence[0] : rsaddinLoad.Hint);
          break;
        }

        if (!needsMcpAddinEvidence && !needsRsaddinEvidence)
        {
          break;
        }

        if (DateTime.UtcNow > deadline)
        {
          timedOut = true;
          break;
        }

        await Task.Delay(retryDelayMs, cancellationToken);
      }
    }

    if (!addinReachable)
    {
      timedOut = timedOut || DateTime.UtcNow > deadline;
    }

    var logDiscovery = BuildLogDiscoveryPayload(packageId, usageLogsRoot, errorLogsRoot, mcpAddinLogEvidence);

    var runtimeReady = addinReachable;
    if (failingStage == VerifyStage.Passed && requireInstalled && !packagePathsOk)
    {
      failingStage = VerifyStage.Runtime;
      lastFailureType = VerifyFailureType.None;
      lastFailureMessage = "Required package installation evidence is missing.";
    }

    if (failingStage == VerifyStage.Passed && IsDefinitiveAddinLoadFailure(mcpAddinLoad))
    {
      if (!licenseReadiness.ok)
      {
        failingStage = VerifyStage.License;
        lastFailureType = VerifyFailureType.RobotStudioLicense;
        lastFailureMessage = licenseReadiness.reason;
      }
      else
      {
        failingStage = VerifyStage.AddinLoad;
        lastFailureType = VerifyFailureType.RobotStudioAddinLoad;
        lastFailureMessage = mcpAddinLoad.Evidence.Count > 0 ? mcpAddinLoad.Evidence[0] : mcpAddinLoad.Hint;
      }
    }

    var mcpAddinLoadOk = !IsDefinitiveAddinLoadFailure(mcpAddinLoad);
    var rsaddinLoadOk = string.IsNullOrWhiteSpace(rsaddinId) || !IsDefinitiveAddinLoadFailure(rsaddinLoad);
    var ready = runtimeReady
        && (!requireInstalled || packagePathsOk)
        && mcpAddinLoadOk
        && rsaddinLoadOk
        && (!requireAddinLoaded || mcpAddinLoad.Ok);
    var elapsedMs = (int)(DateTime.UtcNow - startedAtUtc).TotalMilliseconds;

    var addinPingPayload = runtimeReady
        ? new { ok = true, pipeName = resolvedPipeName, serverName, serverVersion, error = (string?)null }
        : new { ok = false, pipeName = resolvedPipeName, serverName, serverVersion, error = lastPingError };

    var runtimeReadinessPayload = new
    {
      pipeName = resolvedPipeName,
      addinPing = addinPingPayload,
      mcpAddinLoad = ToAddinLoadPayload(mcpAddinLoad),
      rsaddinLoad = ToAddinLoadPayload(rsaddinLoad),
      rsaddinId,
      ready = runtimeReady,
      elapsedMs,
      attempts
    };

    var preLaunchCleanupPayload = new
    {
      staleStateClearedOnStartup = trackedProcess.TryGetProperty("StaleStateClearedOnStartup", out var stale) && stale.GetBoolean(),
      startupReconciliationMessage = trackedProcess.TryGetProperty("StartupReconciliationMessage", out var msg) ? msg.GetString() : null
    };

    var stageDiagnostics = new
    {
      stage = ToFailureStageCode(ready ? VerifyStage.Passed : failingStage),
      lastFailureType = ToFailureTypeCode(lastFailureType),
      lastFailureMessage,
      lastProbeAtUtc,
      progress = new
      {
        currentStage = ToFailureStageCode(ready ? VerifyStage.Passed : failingStage),
        nextStage = failingStage switch
        {
          VerifyStage.Runtime => "health",
          VerifyStage.Health => "mcp_initialize",
          VerifyStage.McpInitialize => "ready",
          VerifyStage.AddinLoad => "ready",
          VerifyStage.License => "ready",
          VerifyStage.Passed => "done",
          _ => "health"
        },
        waitingFor = failingStage switch
        {
          VerifyStage.Runtime => "RobotStudio process identity",
          VerifyStage.Health => "RobotStudio add-in pipe to become reachable",
          VerifyStage.McpInitialize => "MCP initialize to succeed",
          VerifyStage.AddinLoad => "a definitive add-in load outcome",
          VerifyStage.License => "a non-Free RobotStudio license",
          VerifyStage.Passed => "no further waits",
          _ => "RobotStudio process identity"
        },
        satisfiedConditions = new
        {
          processTracked = trackedProcess.ValueKind != JsonValueKind.Null && trackedProcess.ValueKind != JsonValueKind.Undefined,
          logsScanned = mcpAddinLogEvidence.LoadProgressEntries.Count > 0 || mcpAddinLogEvidence.LoadFailureEntries.Count > 0 || mcpAddinLogEvidence.IgnoredFailureEntries.Count > 0,
          licenseChecked = true,
          addinLoadClassified = mcpAddinLoad.Status != "unknown" || rsaddinLoad.Status != "unknown",
          mcpHealthProbed = addinReachable || lastPingError is not null,
          mcpInitialized = addinReachable
        }
      },
      mcpAddinLoad = ToAddinLoadPayload(mcpAddinLoad),
      license = new
      {
        ok = licenseReadiness.ok,
        reason = licenseReadiness.reason,
        found = licenseDiagnostics.Found,
        licenseLevel = licenseDiagnostics.LicenseLevel,
        licenses = licenseDiagnostics.Licenses,
        licenseFeatures = licenseDiagnostics.LicenseFeatures,
        premiumLicenseType = licenseDiagnostics.PremiumLicenseType
      }
    };

    return new
    {
      ready,
      timedOut,
      attempts,
      elapsedMs,
      processStart = processStatus,
      preLaunchCleanup = preLaunchCleanupPayload,
      mcpPackagePaths = packagePathsPayload,
      runtimeReadiness = runtimeReadinessPayload,
      robotStudioLogs = logDiscovery,
      stageDiagnostics,
      effectiveTimeouts = new { timeoutSeconds, retryDelayMs, probeTimeoutMs },
      requireInstalled,
      requireAddinLoaded,
      installationRequiredButMissing = requireInstalled && !packagePathsOk
    };
  }

  internal static PackagePathDiagnostics ScanPackagePaths(string packageId, string? distributionPackagesRoot = null, string? distributionPackagesRoot2 = null)
  {
    var normalizedPackageId = string.IsNullOrWhiteSpace(packageId) ? "RobotStudioMCP" : packageId.Trim();

    var roots = ResolveDistributionPackageRoots(distributionPackagesRoot, distributionPackagesRoot2).ToArray();

    var searchedRoots = new List<string>();
    var matchedPackages = new List<string>();
    var evidence = new List<string>();
    var errors = new List<string>();

    foreach (var root in roots)
    {
      searchedRoots.Add(root);
      if (!Directory.Exists(root))
      {
        continue;
      }

      string[] packageDirectories;
      try
      {
        packageDirectories = Directory.GetDirectories(root);
      }
      catch (Exception ex)
      {
        errors.Add($"Failed to enumerate '{root}': {ex.Message}");
        continue;
      }

      foreach (var packageDir in packageDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
      {
        var directoryName = Path.GetFileName(packageDir) ?? string.Empty;
        if (!directoryName.StartsWith(normalizedPackageId, StringComparison.OrdinalIgnoreCase))
        {
          continue;
        }

        matchedPackages.Add(packageDir);

        var robotStudioFolder = Path.Combine(packageDir, "RobotStudio");
        if (Directory.Exists(robotStudioFolder))
        {
          evidence.Add($"Package '{directoryName}' contains RobotStudio payload folder.");
        }
      }
    }

    return new PackagePathDiagnostics
    {
      Installed = matchedPackages.Count > 0 && evidence.Count > 0,
      Method = "rspak",
      PackageId = normalizedPackageId,
      SearchedRoots = searchedRoots,
      MatchedPackages = matchedPackages,
      Evidence = evidence,
      Errors = errors
    };
  }

  internal static RobotStudioLicenseDiagnostics ParseLatestUsageLogLicenseInfo(string? usageLogsRoot = null)
  {
    var discovery = DiscoverLogs("usage", usageLogsRoot, maxFiles: 1);
    if (discovery.Errors.Count > 0 && discovery.Files.Count == 0)
    {
      return new RobotStudioLicenseDiagnostics
      {
        Found = false,
        Error = string.Join(" | ", discovery.Errors)
      };
    }

    var latest = discovery.Files.Count > 0 ? discovery.Files[0] : null;
    if (latest is null)
    {
      return new RobotStudioLicenseDiagnostics
      {
        Found = false,
        Error = "No usage log files found."
      };
    }

    try
    {
      var header = ReadRobotStudioHeader(latest.Path);
      header.TryGetValue("SessionStartUtc", out var sessionStartUtc);
      header.TryGetValue("AppVersion", out var appVersion);
      header.TryGetValue("LicenseLevel", out var licenseLevel);
      header.TryGetValue("Licenses", out var licenses);
      header.TryGetValue("LicenseFeatures", out var licenseFeatures);
      header.TryGetValue("PremiumLicenseType", out var premiumLicenseType);

      return new RobotStudioLicenseDiagnostics
      {
        Found = !string.IsNullOrWhiteSpace(licenseLevel)
            || !string.IsNullOrWhiteSpace(licenses)
            || !string.IsNullOrWhiteSpace(licenseFeatures)
            || !string.IsNullOrWhiteSpace(premiumLicenseType),
        LogPath = latest.Path,
        SessionStartUtc = sessionStartUtc,
        AppVersion = appVersion,
        LicenseLevel = licenseLevel,
        Licenses = licenses,
        LicenseFeatures = licenseFeatures,
        PremiumLicenseType = premiumLicenseType
      };
    }
    catch (Exception ex)
    {
      return new RobotStudioLicenseDiagnostics
      {
        Found = false,
        LogPath = latest.Path,
        Error = ex.Message
      };
    }
  }

  internal static RobotStudioAddinLoadLogEvidence CollectAddinLoadLogEvidenceFromLatestFiles(
      string packageId,
      string? usageLogsRoot = null,
      string? errorLogsRoot = null,
      int maxFilesPerKind = 2,
      DateTime? notBeforeUtc = null)
  {
    var normalizedPackageId = string.IsNullOrWhiteSpace(packageId) ? "RobotStudioMCP" : packageId.Trim();
    var loadProgressEntries = new List<string>();
    var loadFailureEntries = new List<string>();
    var ignoredFailureEntries = new List<string>();
    string? firstFailureLine = null;
    string? firstFailureEvidence = null;

    var usageFiles = FilterLogsForSessionWindow(
            DiscoverLogs("usage", usageLogsRoot, maxFilesPerKind).Files,
            notBeforeUtc)
        .OrderBy(file => file.LastWriteTimeUtc)
        .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    foreach (var file in usageFiles)
    {
      var insideBenignExceptionBlock = false;

      foreach (var line in ReadAllLinesShared(file.Path))
      {
        if (line.Contains($"AddinAdded|{normalizedPackageId}", StringComparison.OrdinalIgnoreCase)
            || line.Contains($"AddinLoading|{normalizedPackageId}", StringComparison.OrdinalIgnoreCase)
            || line.Contains($"AddinLoaded|{normalizedPackageId}", StringComparison.OrdinalIgnoreCase)
            || line.Contains($"ActivatedPP|{normalizedPackageId}", StringComparison.OrdinalIgnoreCase))
        {
          loadProgressEntries.Add($"{file.Path}: {line}");
        }

        AppendAddinFailureEvidence(
            line,
            normalizedPackageId,
            file.Path,
            loadFailureEntries,
            ignoredFailureEntries,
            ref firstFailureLine,
            ref firstFailureEvidence,
            ref insideBenignExceptionBlock);
      }
    }

    var errorFiles = FilterLogsForSessionWindow(
            DiscoverLogs("error", errorLogsRoot, maxFilesPerKind).Files,
            notBeforeUtc)
        .OrderBy(file => file.LastWriteTimeUtc)
        .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    foreach (var file in errorFiles)
    {
      var insideBenignExceptionBlock = false;

      foreach (var line in ReadAllLinesShared(file.Path))
      {
        AppendAddinFailureEvidence(
            line,
            normalizedPackageId,
            file.Path,
            loadFailureEntries,
            ignoredFailureEntries,
            ref firstFailureLine,
            ref firstFailureEvidence,
            ref insideBenignExceptionBlock);
      }
    }

    return new RobotStudioAddinLoadLogEvidence
    {
      PackageId = normalizedPackageId,
      LoadProgressEntries = loadProgressEntries.Take(20).ToArray(),
      LoadFailureEntries = loadFailureEntries.Take(20).ToArray(),
      IgnoredFailureEntries = ignoredFailureEntries.Take(20).ToArray(),
      FirstFailureLine = firstFailureLine,
      FirstFailureEvidence = firstFailureEvidence
    };
  }

  internal static RobotStudioLogDiscovery DiscoverLogs(string kind, string? rootOverride, int maxFiles)
  {
    var normalizedKind = NormalizeLogKind(kind);
    var root = string.IsNullOrWhiteSpace(rootOverride)
        ? normalizedKind == "usage" ? DefaultUsageLogsRoot : DefaultErrorLogsRoot
        : rootOverride.Trim();

    var files = new List<RobotStudioLogFileInfo>();
    var errors = new List<string>();

    try
    {
      if (!Directory.Exists(root))
      {
        return new RobotStudioLogDiscovery
        {
          Kind = normalizedKind,
          Root = root,
          Files = files,
          Errors = errors
        };
      }

      foreach (var file in new DirectoryInfo(root)
                   .EnumerateFiles("*.rslog", SearchOption.TopDirectoryOnly)
                   .OrderByDescending(f => f.LastWriteTimeUtc)
                   .Take(maxFiles))
      {
        files.Add(new RobotStudioLogFileInfo
        {
          Path = file.FullName,
          LastWriteTimeUtc = file.LastWriteTimeUtc,
          LengthBytes = file.Length
        });
      }
    }
    catch (Exception ex)
    {
      errors.Add($"Failed to enumerate '{root}': {ex.Message}");
    }

    return new RobotStudioLogDiscovery
    {
      Kind = normalizedKind,
      Root = root,
      Files = files,
      Errors = errors
    };
  }

  private static string NormalizeLogKind(string kind)
  {
    var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
    return normalized is "usage" or "error" ? normalized : "all";
  }

  private static IEnumerable<RobotStudioLogFileInfo> FilterLogsForSessionWindow(
      IEnumerable<RobotStudioLogFileInfo> files,
      DateTime? notBeforeUtc)
  {
    foreach (var file in files)
    {
      if (ShouldIncludeLogFileForSessionWindow(file, notBeforeUtc))
      {
        yield return file;
      }
    }
  }

  private static bool ShouldIncludeLogFileForSessionWindow(
      RobotStudioLogFileInfo file,
      DateTime? notBeforeUtc)
  {
    if (notBeforeUtc is null)
    {
      return true;
    }

    var cutoffUtc = notBeforeUtc.Value;
    if (file.LastWriteTimeUtc >= cutoffUtc)
    {
      return true;
    }

    if (TryParseRobotStudioTimestampFromLogFileName(file.Path, out var fileNameTimestampUtc)
        && fileNameTimestampUtc >= cutoffUtc)
    {
      return true;
    }

    return false;
  }

  private static bool TryParseRobotStudioTimestampFromLogFileName(string logPath, out DateTime timestampUtc)
  {
    timestampUtc = default;

    var fileName = Path.GetFileNameWithoutExtension(logPath) ?? string.Empty;
    var parts = fileName.Split('-', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 3)
    {
      return false;
    }

    var datePart = parts[1];
    var timePart = parts[2];
    if (datePart.Length != 8 || timePart.Length != 6)
    {
      return false;
    }

    if (!DateTime.TryParseExact(
            datePart + timePart,
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed))
    {
      return false;
    }

    timestampUtc = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
    return true;
  }

  private static Dictionary<string, string> ReadRobotStudioHeader(string logPath)
  {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var inHeader = false;

    foreach (var rawLine in ReadAllLinesShared(logPath))
    {
      var line = rawLine.Trim();
      if (string.Equals(line, "----HEADER BEGIN----", StringComparison.OrdinalIgnoreCase))
      {
        inHeader = true;
        continue;
      }

      if (string.Equals(line, "----HEADER END----", StringComparison.OrdinalIgnoreCase))
      {
        break;
      }

      if (!inHeader)
      {
        continue;
      }

      var separator = line.IndexOf('=');
      if (separator <= 0)
      {
        continue;
      }

      var key = line[..separator].Trim();
      var value = line[(separator + 1)..].Trim();
      if (!string.IsNullOrWhiteSpace(key))
      {
        result[key] = value;
      }
    }

    return result;
  }

  private static string? GetRsaddinId(string? rsaddinPath)
  {
    var normalized = (rsaddinPath ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(normalized))
    {
      return null;
    }

    var fileName = Path.GetFileNameWithoutExtension(normalized);
    return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
  }

  private static object BuildLogDiscoveryPayload(string packageId, string? usageLogsRoot, string? errorLogsRoot, RobotStudioAddinLoadLogEvidence? precomputedEvidence = null)
  {
    var usage = DiscoverLogs("usage", usageLogsRoot, maxFiles: 3);
    var error = DiscoverLogs("error", errorLogsRoot, maxFiles: 3);
    var addinLoadEvidence = precomputedEvidence ?? CollectAddinLoadLogEvidenceFromLatestFiles(packageId, usageLogsRoot, errorLogsRoot, maxFilesPerKind: 2);

    return new
    {
      usageLogs = ToLogDiscoveryPayload(usage),
      errorLogs = ToLogDiscoveryPayload(error),
      addinLoadEvidence = new
      {
        packageId = addinLoadEvidence.PackageId,
        loadProgressEntries = addinLoadEvidence.LoadProgressEntries,
        loadFailureEntries = addinLoadEvidence.LoadFailureEntries,
        ignoredFailureEntries = addinLoadEvidence.IgnoredFailureEntries
      }
    };
  }

  internal static (string? addinType, string? licenseFeature) ParseRsaddinManifest(string? rsaddinPath)
  {
    if (string.IsNullOrWhiteSpace(rsaddinPath) || !File.Exists(rsaddinPath))
    {
      return (null, null);
    }

    try
    {
      var content = File.ReadAllText(rsaddinPath);
      var typeMatch = System.Text.RegularExpressions.Regex.Match(content, @"<AddInType>\s*([^<]+)\s*</AddInType>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      var featureMatch = System.Text.RegularExpressions.Regex.Match(content, @"<LicenseFeature>\s*([^<]+)\s*</LicenseFeature>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

      return (
          typeMatch.Success ? typeMatch.Groups[1].Value.Trim() : null,
          featureMatch.Success ? featureMatch.Groups[1].Value.Trim() : null
      );
    }
    catch
    {
      return (null, null);
    }
  }

  internal static RobotStudioAddinLoadState GetAddinLoadState(
      string packageId,
      string? rsaddinPath,
      string? usageLogsRoot,
      string? errorLogsRoot,
      DateTime? notBeforeUtc,
      RobotStudioLicenseDiagnostics? licenseDiagnostics = null)
  {
    var mcpEvidence = CollectAddinLoadLogEvidenceFromLatestFiles(
        packageId,
        usageLogsRoot,
        errorLogsRoot,
        maxFilesPerKind: 2,
        notBeforeUtc: notBeforeUtc);
    var mcpLoad = ClassifyAddinLoad(mcpEvidence);

    var rsaddinId = GetRsaddinId(rsaddinPath);
    var requestedEvidence = string.IsNullOrWhiteSpace(rsaddinId)
        ? null
        : CollectAddinLoadLogEvidenceFromLatestFiles(
            rsaddinId,
            usageLogsRoot,
            errorLogsRoot,
            maxFilesPerKind: 2,
            notBeforeUtc: notBeforeUtc);
    var requestedLoad = requestedEvidence is null
        ? new RobotStudioAddinLoadClassification
        {
          Code = "ADDIN_NOT_REQUESTED",
          Status = "not_requested",
          Ok = true,
          Evidence = Array.Empty<string>(),
          Hint = "No rsaddin path was provided."
        }
        : ClassifyAddinLoad(requestedEvidence);

    if (requestedEvidence is not null && !string.IsNullOrWhiteSpace(rsaddinPath))
    {
      var (_, manifestFeature) = ParseRsaddinManifest(rsaddinPath);

      if (!string.IsNullOrWhiteSpace(manifestFeature)
          && licenseDiagnostics is not null
          && licenseDiagnostics.Found
          && !string.Equals(licenseDiagnostics.LicenseLevel?.Trim(), "Trial", StringComparison.OrdinalIgnoreCase))
      {
        var availableFeatures = (licenseDiagnostics.LicenseFeatures ?? string.Empty)
            .Split(LicenseFeatureSeparators, StringSplitOptions.RemoveEmptyEntries);
        var hasFeature = availableFeatures.Any(f => string.Equals(f.Trim(), manifestFeature, StringComparison.OrdinalIgnoreCase));

        if (!hasFeature && requestedLoad.Status is not "loaded" and not "activated")
        {
          requestedLoad = new RobotStudioAddinLoadClassification
          {
            Code = "ADDIN_LOAD_LICENSE_FEATURE_MISSING",
            Status = "license_feature_missing",
            Ok = false,
            Evidence = requestedLoad.Evidence.Count > 0 ? requestedLoad.Evidence : new[] { $"Required license feature: {manifestFeature}" },
            Hint = $"Add-in requires license feature '{manifestFeature}', which was not found in RobotStudio license features ({licenseDiagnostics.LicenseFeatures ?? "none"}). RobotStudio silently skips loading add-ins with unfulfilled license features."
          };
        }
      }
    }

    return new RobotStudioAddinLoadState
    {
      McpEvidence = mcpEvidence,
      McpLoad = mcpLoad,
      RsaddinLoad = requestedLoad,
      RsaddinId = rsaddinId
    };
  }

  internal static RobotStudioAddinLoadClassification ClassifyAddinLoad(RobotStudioAddinLoadLogEvidence addinLoadEvidence)
  {
    var activatedSignal = addinLoadEvidence.LoadProgressEntries.FirstOrDefault(line => line.Contains($"ActivatedPP|{addinLoadEvidence.PackageId}", StringComparison.OrdinalIgnoreCase));
    var loadedSignal = addinLoadEvidence.LoadProgressEntries.FirstOrDefault(line => line.Contains($"AddinLoaded|{addinLoadEvidence.PackageId}", StringComparison.OrdinalIgnoreCase));
    var firstFailureEvidence = addinLoadEvidence.FirstFailureEvidence
        ?? (addinLoadEvidence.LoadFailureEntries.Count > 0 ? addinLoadEvidence.LoadFailureEntries[0] : null);
    var hasEntrypointFailure = !string.IsNullOrWhiteSpace(firstFailureEvidence);
    var hasTargetFailure = addinLoadEvidence.LoadFailureEntries.Count > 0;
    var hasNoProgressSignal = addinLoadEvidence.LoadProgressEntries.Count == 0;

    var startupFailureEvidence = SelectStartupFailureEvidence(addinLoadEvidence) ?? firstFailureEvidence;

    if (hasEntrypointFailure && (!string.IsNullOrWhiteSpace(loadedSignal) || !string.IsNullOrWhiteSpace(activatedSignal)))
    {
      var successSignal = activatedSignal ?? loadedSignal!;
      return new RobotStudioAddinLoadClassification
      {
        Code = "ADDIN_LOAD_SUCCEEDED",
        Status = activatedSignal is not null ? "activated" : "loaded",
        Ok = true,
        Evidence = new[] { firstFailureEvidence!, successSignal },
        Hint = "Addin load detected after a startup exception; the initial failure is preserved as primary evidence.",
        FirstAddinEntrypointException = addinLoadEvidence.FirstFailureLine,
        StartupFailureReason = StripLogEvidencePrefix(startupFailureEvidence),
        HadStartupException = true
      };
    }

    if (hasTargetFailure || (hasEntrypointFailure && hasNoProgressSignal))
    {
      var failureEvidence = hasTargetFailure
          ? addinLoadEvidence.LoadFailureEntries.Take(3).ToArray()
          : new[] { firstFailureEvidence! };
      return new RobotStudioAddinLoadClassification
      {
        Code = hasTargetFailure ? "ADDIN_LOAD_FAILED" : "ADDIN_LOAD_FAILED_WITH_EXCEPTION",
        Status = "failed",
        Ok = false,
        Evidence = failureEvidence,
        Hint = hasTargetFailure
            ? "RobotStudio ErrorLogs contain target add-in load failure entries. Use MCP tool 'robotstudio_watchdog_read_robot_studio_log_tail' for details."
            : "RobotStudio logs contain an add-in startup exception before AddinLoaded was observed.",
        FirstAddinEntrypointException = addinLoadEvidence.FirstFailureLine,
        StartupFailureReason = StripLogEvidencePrefix(startupFailureEvidence),
        HadStartupException = false
      };
    }

    if (!string.IsNullOrWhiteSpace(activatedSignal))
    {
      return new RobotStudioAddinLoadClassification
      {
        Code = "ADDIN_LOAD_SUCCEEDED",
        Status = "activated",
        Ok = true,
        Evidence = new[] { activatedSignal },
        Hint = "ActivatedPP entry detected in latest-session usage logs."
      };
    }

    if (!string.IsNullOrWhiteSpace(loadedSignal))
    {
      return new RobotStudioAddinLoadClassification
      {
        Code = "ADDIN_LOAD_SUCCEEDED",
        Status = "loaded",
        Ok = true,
        Evidence = new[] { loadedSignal },
        Hint = "AddinLoaded entry detected in latest-session usage logs."
      };
    }

    var loadingSignal = addinLoadEvidence.LoadProgressEntries.FirstOrDefault(line => line.Contains($"AddinLoading|{addinLoadEvidence.PackageId}", StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(loadingSignal))
    {
      return new RobotStudioAddinLoadClassification
      {
        Code = "ADDIN_LOAD_UNKNOWN",
        Status = "loading",
        Ok = false,
        Evidence = new[] { loadingSignal },
        Hint = "AddinLoading entry detected but AddinLoaded not yet observed."
      };
    }

    var addedSignal = addinLoadEvidence.LoadProgressEntries.FirstOrDefault(line => line.Contains($"AddinAdded|{addinLoadEvidence.PackageId}", StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(addedSignal))
    {
      if (IsPowerPacAddedEntry(addedSignal))
      {
        return new RobotStudioAddinLoadClassification
        {
          Code = "ADDIN_AWAITING_ACTIVATION",
          Status = "awaiting manual activation",
          Ok = true,
          Evidence = new[] { addedSignal },
          Hint = "AddinAdded entry detected for PowerPac; awaiting manual activation in RobotStudio UI."
        };
      }

      return new RobotStudioAddinLoadClassification
      {
        Code = "ADDIN_LOAD_UNKNOWN",
        Status = "added",
        Ok = false,
        Evidence = new[] { addedSignal },
        Hint = "AddinAdded entry detected but AddinLoaded not yet observed."
      };
    }

    return new RobotStudioAddinLoadClassification
    {
      Code = "ADDIN_LOAD_UNKNOWN",
      Status = "unknown",
      Ok = false,
      Evidence = Array.Empty<string>(),
      Hint = "No definitive add-in load entries found in recent logs."
    };
  }

  private static object ToAddinLoadPayload(RobotStudioAddinLoadClassification addinLoad)
  {
    return new
    {
      code = addinLoad.Code,
      status = addinLoad.Status,
      ok = addinLoad.Ok,
      evidence = addinLoad.Evidence,
      hint = addinLoad.Hint,
      firstAddinEntrypointException = addinLoad.FirstAddinEntrypointException,
      startupFailureReason = addinLoad.StartupFailureReason,
      hadStartupException = addinLoad.HadStartupException
    };
  }

  private static string? SelectStartupFailureEvidence(RobotStudioAddinLoadLogEvidence addinLoadEvidence)
  {
    static int Rank(string entry)
    {
      if (entry.Contains("Missing or invalid", StringComparison.OrdinalIgnoreCase))
      {
        return 0;
      }

      if (entry.Contains("InvalidOperationException", StringComparison.OrdinalIgnoreCase))
      {
        return 1;
      }

      if (entry.Contains("TargetInvocationException", StringComparison.OrdinalIgnoreCase))
      {
        return 2;
      }

      if (entry.Contains("Exception has been thrown by the target of an invocation", StringComparison.OrdinalIgnoreCase))
      {
        return 3;
      }

      if (entry.Contains("RobotStudio .NET exception", StringComparison.OrdinalIgnoreCase))
      {
        return 4;
      }

      return int.MaxValue;
    }

    return addinLoadEvidence.LoadFailureEntries.Concat(addinLoadEvidence.IgnoredFailureEntries)
        .OrderBy(Rank)
        .FirstOrDefault()
        ?? (addinLoadEvidence.LoadFailureEntries.Count > 0 ? addinLoadEvidence.LoadFailureEntries[0] : null);
  }

  private static bool IsDefinitiveAddinLoadFailure(RobotStudioAddinLoadClassification addinLoad)
  {
    return string.Equals(addinLoad.Status, "failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(addinLoad.Status, "license_feature_missing", StringComparison.OrdinalIgnoreCase)
        || addinLoad.Code is "ADDIN_LOAD_FAILED" or "ADDIN_LOAD_FAILED_WITH_EXCEPTION" or "ADDIN_LOAD_LICENSE_FEATURE_MISSING"
        || addinLoad.HadStartupException;
  }

  private static void AppendAddinFailureEvidence(
      string line,
      string normalizedPackageId,
      string filePath,
      List<string> loadFailureEntries,
      List<string> ignoredFailureEntries,
      ref string? firstFailureLine,
      ref string? firstFailureEvidence,
      ref bool insideBenignExceptionBlock)
  {
    var evidence = $"{filePath}: {line}";

    // Once a benign Silent-marked exception's summary line is seen, its whole
    // "----EXCEPTION BEGIN----"..."----EXCEPTION END----" dump must stay benign too -
    // the inner stack-frame lines independently match LooksLikeAddinFailure (they
    // contain the word "Exception" as part of .NET type/parameter names) and would
    // otherwise become a *new*, unfiltered firstFailureLine right after the real one
    // was correctly suppressed (found via live verification: RobotStudio's
    // CodeSnippetManager XmlException dump re-triggered this on its "----EXCEPTION
    // BEGIN----" line even though the summary line itself was filtered).
    if (insideBenignExceptionBlock)
    {
      ignoredFailureEntries.Add(evidence);
      if (IsExceptionBlockEndMarker(line))
      {
        insideBenignExceptionBlock = false;
      }

      return;
    }

    if (!LooksLikeAddinFailure(line))
    {
      return;
    }

    if (IsBenignPlatformLogEntry(line) && !MentionsTargetAddin(line, normalizedPackageId))
    {
      ignoredFailureEntries.Add(evidence);
      if (IsSilentPlatformException(line))
      {
        insideBenignExceptionBlock = true;
      }

      return;
    }

    firstFailureLine ??= line;
    firstFailureEvidence ??= evidence;

    if (MentionsTargetAddin(line, normalizedPackageId) || LooksLikeGenericAddinLoadFailure(line))
    {
      loadFailureEntries.Add(evidence);
      return;
    }

    ignoredFailureEntries.Add(evidence);
  }

  private static bool IsExceptionBlockEndMarker(string line)
  {
    return string.Equals(line.Trim(), "----EXCEPTION END----", StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsBenignPlatformLogEntry(string line)
  {
    return line.Contains("UIThreadWatchdog", StringComparison.OrdinalIgnoreCase)
        || line.Contains("ABB.Kairos.ClientApi.AiService.ApiException", StringComparison.OrdinalIgnoreCase)
        || IsSilentPlatformException(line);
  }

  /// <summary>
  /// RobotStudio tags its own generic unhandled-exception telemetry as
  /// "&lt;timestamp&gt;|Exception|&lt;guid&gt;|&lt;TypeName&gt;|Silent" (vs. "|UserVisible"
  /// for exceptions it shows to the user). This is RobotStudio's own judgment that
  /// the exception didn't warrant interrupting the user - a reasonable proxy for
  /// "not an add-in load failure" for any exception that doesn't separately mention
  /// the target add-in (MentionsTargetAddin is still checked by the caller).
  /// </summary>
  private static bool IsSilentPlatformException(string line)
  {
    return line.Contains("|Exception|", StringComparison.OrdinalIgnoreCase)
        && line.TrimEnd().EndsWith("|Silent", StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsPowerPacAddedEntry(string addedEvidence)
  {
    var rawLine = StripLogEvidencePrefix(addedEvidence) ?? addedEvidence;
    var fields = rawLine.Split('|');
    return fields.Length > 0 && string.Equals(fields[^1].Trim(), "PowerPac", StringComparison.OrdinalIgnoreCase);
  }

  private static bool MentionsTargetAddin(string line, string normalizedPackageId)
  {
    return line.Contains(normalizedPackageId, StringComparison.OrdinalIgnoreCase)
        || line.Contains($"{normalizedPackageId}.rsaddin", StringComparison.OrdinalIgnoreCase);
  }

  private static bool LooksLikeAddinFailure(string line)
  {
    return line.Contains("Failed to load", StringComparison.OrdinalIgnoreCase)
        || line.Contains("|Error|", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Exception", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Missing or invalid ROBOTSTUDIO_DEMO_MODE", StringComparison.OrdinalIgnoreCase)
        || line.Contains("ROBOTSTUDIO_DEMO_MODE", StringComparison.OrdinalIgnoreCase);
  }

  private static bool LooksLikeGenericAddinLoadFailure(string line)
  {
    return line.Contains("ReflectionTypeLoadException", StringComparison.OrdinalIgnoreCase)
        || line.Contains("LoaderException", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Unable to load one or more of the requested types", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Could not load file or assembly", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Assembly.GetTypes", StringComparison.OrdinalIgnoreCase)
        || line.Contains("RobotStudio.ExtensionManagement.Addin.TryLoad", StringComparison.OrdinalIgnoreCase);
  }

  private static string? StripLogEvidencePrefix(string? evidence)
  {
    if (string.IsNullOrWhiteSpace(evidence))
    {
      return null;
    }

    var separator = evidence.IndexOf(": ", StringComparison.Ordinal);
    return separator >= 0 && separator + 2 < evidence.Length
        ? evidence[(separator + 2)..].Trim()
        : evidence.Trim();
  }

  private static IEnumerable<string> ReadAllLinesShared(string path)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

    while (!reader.EndOfStream)
    {
      yield return reader.ReadLine() ?? string.Empty;
    }
  }

  internal static (bool ok, string reason) EvaluateLicenseReadinessForAddinLoad(RobotStudioLicenseDiagnostics diagnostics)
  {
    if (!diagnostics.Found)
    {
      return (false, string.IsNullOrWhiteSpace(diagnostics.Error)
          ? "License info unavailable from RobotStudio usage logs."
          : $"License info unavailable: {diagnostics.Error}");
    }

    if (string.IsNullOrWhiteSpace(diagnostics.LicenseLevel))
    {
      return (false, "RobotStudio license level is not reported in usage logs.");
    }

    if (string.Equals(diagnostics.LicenseLevel.Trim(), "Free", StringComparison.OrdinalIgnoreCase))
    {
      return (false, "RobotStudio license level is Free, which does not permit add-in startup.");
    }

    return (true, "License info indicates a non-Free license level.");
  }

  private static string ToFailureStageCode(VerifyStage stage) => stage switch
  {
    VerifyStage.Passed => "passed",
    VerifyStage.Runtime => "runtime",
    VerifyStage.Health => "health",
    VerifyStage.McpInitialize => "mcp_initialize",
    VerifyStage.AddinLoad => "addin_load",
    VerifyStage.License => "license",
    _ => "runtime"
  };

  private static string ToFailureTypeCode(VerifyFailureType failureType) => failureType switch
  {
    VerifyFailureType.None => "none",
    VerifyFailureType.HealthProbe => "health_probe",
    VerifyFailureType.McpInitialize => "mcp_initialize",
    VerifyFailureType.RobotStudioLicense => "robotstudio_license",
    VerifyFailureType.RobotStudioAddinLoad => "robotstudio_addin_load",
    _ => "none"
  };

  internal static IEnumerable<string> ResolveDistributionPackageRoots(string? distributionPackagesRoot, string? distributionPackagesRoot2)
  {
    var overrides = new[] { distributionPackagesRoot, distributionPackagesRoot2 }
        .Select(root => (root ?? string.Empty).Trim())
        .Where(root => !string.IsNullOrWhiteSpace(root))
        .Select(root => root!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (overrides.Length > 0)
    {
      return overrides;
    }

    return new[]
    {
      DefaultUserDistributionPackagesRoot,
      DefaultUserDistributionPackagesRoot2
    }
    .Where(root => !string.IsNullOrWhiteSpace(root))
    .Select(root => root!)
    .Distinct(StringComparer.OrdinalIgnoreCase);
  }

  private static object ToInstallationPayload(PackagePathDiagnostics diagnostics) => new
  {
    installed = diagnostics.Installed,
    method = diagnostics.Method,
    packageId = diagnostics.PackageId,
    searchedRoots = diagnostics.SearchedRoots,
    matchedPackages = diagnostics.MatchedPackages,
    evidence = diagnostics.Evidence,
    errors = diagnostics.Errors
  };

  private static object ToPackagePathsPayload(PackagePathDiagnostics diagnostics) => new
  {
    installed = diagnostics.Installed,
    packageId = diagnostics.PackageId,
    searchedRoots = diagnostics.SearchedRoots,
    matchedPackages = diagnostics.MatchedPackages,
    evidence = diagnostics.Evidence,
    errors = diagnostics.Errors
  };

  private static object ToLogDiscoveryPayload(RobotStudioLogDiscovery discovery) => new
  {
    kind = discovery.Kind,
    root = discovery.Root,
    files = discovery.Files.Select(file => new
    {
      path = file.Path,
      lastWriteTimeUtc = file.LastWriteTimeUtc,
      lengthBytes = file.LengthBytes
    }).ToArray(),
    errors = discovery.Errors
  };

  // The add-in exposes its tools over a plain named pipe, not a real MCP endpoint (see AddinPipeServer);
  // this watchdog process is the only one that speaks real MCP to external clients. These two helpers
  // are the internal client side of that same-build, same-version protocol: list_tools doubles as the
  // "is the add-in up and responsive" probe (no separate initialize handshake -- there's no version
  // negotiation to do between two halves of the same release), and call_tool forwards a single add-in
  // tool invocation and relays its result back untouched.
  private static async Task<NamedPipeClientStream> ConnectAddinPipeAsync(string pipeName, int probeTimeoutMs, CancellationToken cancellationToken)
  {
    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    connectCts.CancelAfter(probeTimeoutMs);

    try
    {
      await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
      return pipe;
    }
    catch
    {
      pipe.Dispose();
      throw;
    }
  }

  private static async Task<string?> SendAddinPipeRequestAsync(string pipeName, object request, int probeTimeoutMs, CancellationToken cancellationToken)
  {
    await using var pipe = await ConnectAddinPipeAsync(pipeName, probeTimeoutMs, cancellationToken).ConfigureAwait(false);

    var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);

    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    readCts.CancelAfter(probeTimeoutMs);
    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
    return await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
  }

  private static async Task<(bool ok, int? toolCount, string? serverName, string? serverVersion, string? error)> PingAddinPipe(
      string pipeName, int probeTimeoutMs, CancellationToken cancellationToken = default)
  {
    try
    {
      var line = await SendAddinPipeRequestAsync(pipeName, new { id = 1, method = "list_tools" }, probeTimeoutMs, cancellationToken).ConfigureAwait(false);
      if (string.IsNullOrWhiteSpace(line))
      {
        return (false, null, null, null, "Pipe closed without a response.");
      }

      using var document = JsonDocument.Parse(line);
      var root = document.RootElement;
      if (root.TryGetProperty("error", out var errorElement))
      {
        var message = errorElement.TryGetProperty("message", out var msgElement) ? msgElement.GetString() : "Unknown error.";
        return (false, null, null, null, message);
      }

      if (!root.TryGetProperty("result", out var result))
      {
        return (false, null, null, null, "Response missing result.");
      }

      var toolCount = result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array ? tools.GetArrayLength() : (int?)null;
      var serverName = result.TryGetProperty("serverName", out var sn) ? sn.GetString() : null;
      var serverVersion = result.TryGetProperty("serverVersion", out var sv) ? sv.GetString() : null;
      return (true, toolCount, serverName, serverVersion, null);
    }
    catch (Exception ex)
    {
      return (false, null, null, null, ex.Message);
    }
  }

  private static async Task<object> CallAddinTool(string pipeName, string toolName, int probeTimeoutMs, CancellationToken cancellationToken = default)
  {
    try
    {
      var request = new { id = 1, method = "call_tool", @params = new { name = toolName, arguments = new Dictionary<string, object>() } };
      var line = await SendAddinPipeRequestAsync(pipeName, request, probeTimeoutMs, cancellationToken).ConfigureAwait(false);
      if (string.IsNullOrWhiteSpace(line))
      {
        return new { ok = false, error = "Pipe closed without a response." };
      }

      using var document = JsonDocument.Parse(line);
      var root = document.RootElement;
      if (root.TryGetProperty("error", out var errorElement))
      {
        var message = errorElement.TryGetProperty("message", out var msgElement) ? msgElement.GetString() : "Unknown error.";
        return new { ok = false, error = message };
      }

      if (!root.TryGetProperty("result", out var result))
      {
        return new { ok = false, error = "Response missing result." };
      }

      var isError = result.TryGetProperty("isError", out var isErrorElement) && isErrorElement.ValueKind == JsonValueKind.True;
      var structuredContent = result.TryGetProperty("structuredContent", out var scElement) && scElement.ValueKind == JsonValueKind.Object
          ? JsonSerializer.Deserialize<Dictionary<string, object>>(scElement.GetRawText())
          : null;

      return new { ok = !isError, structuredContent };
    }
    catch (Exception ex)
    {
      return new { ok = false, error = ex.Message, exceptionType = ex.GetType().Name };
    }
  }
}
