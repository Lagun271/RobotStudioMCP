using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RobotStudioMCP.Watchdog;

public interface IRobotStudioProcessLauncher
{
  Process? Start(ProcessStartInfo startInfo);
}

public interface IRobotStudioProcessKiller
{
  object KillAllByName(string processName);
}

internal sealed class RobotStudioProcessLauncher : IRobotStudioProcessLauncher
{
  public Process? Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}

internal sealed class RobotStudioProcessKiller : IRobotStudioProcessKiller
{
  public object KillAllByName(string processName)
  {
    var killed = new List<object>();
    var errors = new List<string>();

    Process[] processes;
    try
    {
      processes = Process.GetProcessesByName(processName);
    }
    catch (Exception ex)
    {
      return new
      {
        processName,
        requested = true,
        killed,
        errors = new[] { $"Failed to enumerate '{processName}': {ex.Message}" }
      };
    }

    foreach (var process in processes)
    {
      try
      {
        var pid = process.Id;
        DateTime? startTimeUtc = null;
        try { startTimeUtc = process.StartTime.ToUniversalTime(); } catch { }

        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
        killed.Add(new { processId = pid, startTimeUtc });
      }
      catch (Exception ex)
      {
        errors.Add($"Failed to kill '{processName}' (PID {SafeReadProcessId(process)}): {ex.Message}");
      }
    }

    return new
    {
      processName,
      requested = true,
      killed,
      errors
    };
  }

  private static int? SafeReadProcessId(Process process)
  {
    try { return process.Id; } catch { return null; }
  }
}

[McpServerToolType]
public sealed class RobotStudioLifecycleTools
{
  private readonly IRobotStudioProcessStateStore _stateStore;
  private readonly IRobotStudioProcessLauncher _processLauncher;
  private readonly IRobotStudioProcessKiller _processKiller;

  public RobotStudioLifecycleTools(IRobotStudioProcessStateStore stateStore)
      : this(stateStore, new RobotStudioProcessLauncher(), new RobotStudioProcessKiller())
  {
  }

  internal RobotStudioLifecycleTools(IRobotStudioProcessStateStore stateStore, IRobotStudioProcessLauncher processLauncher)
      : this(stateStore, processLauncher, new RobotStudioProcessKiller())
  {
  }

  internal RobotStudioLifecycleTools(IRobotStudioProcessStateStore stateStore, IRobotStudioProcessLauncher processLauncher, IRobotStudioProcessKiller processKiller)
  {
    _stateStore = stateStore;
    _processLauncher = processLauncher;
    _processKiller = processKiller;
  }

  [McpServerTool(Destructive = true, Title = "Start RobotStudio"), Description("Start RobotStudio and track the exact started PID. Optional additional `.rsaddin` and launch arguments only apply when starting a new process.")]
  public async Task<object> StartRobotStudioProcess(
      [Description("Full path to RobotStudio.exe. If omitted, defaults to Program Files (x86) path, then PATH lookup.")]
        string? robotStudioExePath = null,
      [Description("Optional absolute full path to an additional `.rsaddin` file to load via /addin alongside the installed MCP add-in. Its assembly must sit in the same folder (e.g. the build output dir, not the project source tree).")]
        string? rsaddinPath = null,
      [Description("Optional absolute full path to a RobotStudio station/project file to open during launch.")]
        string? stationPath = null,
      [Description("Optional process-scoped environment variables applied only to the launched RobotStudio process.")]
        Dictionary<string, string>? environment = null,
      [Description("If true, kill all RobotStudio processes before launch.")]
        bool killAllRobotStudio = false,
      [Description("If true, kill all RobVC processes before launch.")]
        bool killAllRobVC = false,
      [Description("Maximum seconds to wait for startup confirmation.")]
        int timeoutSeconds = 30,
      IProgress<ProgressNotificationValue>? progress = null,
      CancellationToken cancellationToken = default)
  {
    timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 180);

    object? killSummary = null;
    if (killAllRobotStudio || killAllRobVC)
    {
      var actions = new List<object>();
      if (killAllRobotStudio)
      {
        actions.Add(_processKiller.KillAllByName("RobotStudio"));
      }

      if (killAllRobVC)
      {
        actions.Add(_processKiller.KillAllByName("RobVC"));
      }

      killSummary = new
      {
        killAllRobotStudio,
        killAllRobVC,
        actions
      };

      _stateStore.ClearTrackedProcess("Cleared tracked RobotStudio state before launch because killAll options were requested.");

      var startupCooldownAfterKillMs = RobotStudioWatchdogSettings.Current.StartupCooldownAfterKillMs;
      if (startupCooldownAfterKillMs > 0)
      {
        await Task.Delay(startupCooldownAfterKillMs, cancellationToken);
      }
    }

    var normalizedRsaddinPath = string.IsNullOrWhiteSpace(rsaddinPath) ? null : rsaddinPath.Trim();
    var requestedStationPath = string.IsNullOrWhiteSpace(stationPath) ? null : stationPath.Trim();

    if (_stateStore.TryGetTrackedLiveProcess(out var existing, out _))
    {
      return new
      {
        started = false,
        alreadyRunning = true,
        processId = existing.Id,
        startTimeUtc = existing.StartTime.ToUniversalTime(),
        rsaddinPath = normalizedRsaddinPath,
        stationPath = requestedStationPath,
        killSummary,
        message = "A previously tracked RobotStudio process is already running."
      };
    }

    var resolvedExePath = ResolveRobotStudioExe(robotStudioExePath);
    string args = "/LoadAddins";

    if (!string.IsNullOrWhiteSpace(normalizedRsaddinPath))
    {
      if (!Path.IsPathRooted(normalizedRsaddinPath))
      {
        return new
        {
          started = false,
          error = "The 'rsaddinPath' argument must be an absolute path.",
          rsaddinPath = normalizedRsaddinPath,
          stationPath = requestedStationPath,
          killSummary
        };
      }

      if (!File.Exists(normalizedRsaddinPath))
      {
        return new
        {
          started = false,
          error = "The .rsaddin file was not found.",
          rsaddinPath = normalizedRsaddinPath,
          stationPath = requestedStationPath,
          killSummary
        };
      }

      args = $"/addin:\"{normalizedRsaddinPath}\"";
    }

    args = string.IsNullOrWhiteSpace(args) ? "/LoadAddins" : $"{args} /LoadAddins";

    if (!string.IsNullOrWhiteSpace(requestedStationPath))
    {
      if (!Path.IsPathRooted(requestedStationPath))
      {
        return new
        {
          started = false,
          error = "The 'stationPath' argument must be an absolute path.",
          rsaddinPath = normalizedRsaddinPath,
          stationPath = requestedStationPath,
          killSummary
        };
      }

      if (!File.Exists(requestedStationPath))
      {
        return new
        {
          started = false,
          error = "The station file was not found.",
          rsaddinPath = normalizedRsaddinPath,
          stationPath = requestedStationPath,
          killSummary
        };
      }

      args = string.IsNullOrWhiteSpace(args)
          ? $"\"{requestedStationPath}\""
          : $"{args} \"{requestedStationPath}\"";
    }

    var requestTimeUtc = DateTime.UtcNow;

    var normalizedEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (environment is not null)
    {
      foreach (var pair in environment)
      {
        var key = (pair.Key ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key)) continue;
        normalizedEnvironment[key] = pair.Value ?? string.Empty;
      }
    }

    Process? startedProcess;
    try
    {
      var useShellExecute = normalizedEnvironment.Count == 0;
      var startInfo = new ProcessStartInfo
      {
        FileName = resolvedExePath,
        Arguments = args,
        UseShellExecute = useShellExecute,
        WorkingDirectory = Path.GetDirectoryName(resolvedExePath) ?? Environment.CurrentDirectory
      };

      if (!useShellExecute)
      {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;

        foreach (var pair in normalizedEnvironment)
        {
          startInfo.Environment[pair.Key] = pair.Value;
        }
      }

      startedProcess = _processLauncher.Start(startInfo);
      if (startedProcess is not null && !useShellExecute)
      {
        // Drains stdout/stderr for the launched process's whole lifetime, independent of this
        // request's cancellation -- cancelling the tool call must not stop draining and risk the
        // child process blocking on a full pipe buffer. CancellationToken.None is intentional.
        _ = Task.Run(async () =>
        {
          try { await startedProcess.StandardOutput.ReadToEndAsync(); } catch { }
        }, CancellationToken.None);
        _ = Task.Run(async () =>
        {
          try { await startedProcess.StandardError.ReadToEndAsync(); } catch { }
        }, CancellationToken.None);
      }
    }
    catch (Exception ex)
    {
      return new
      {
        started = false,
        error = $"Failed to start RobotStudio: {ex.Message}",
        exePath = resolvedExePath,
        rsaddinPath = normalizedRsaddinPath,
        stationPath = requestedStationPath,
        arguments = args,
        environment = normalizedEnvironment,
        killSummary
      };
    }

    var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    var pid = startedProcess?.Id;

    if (pid is null)
    {
      pid = TryFindRecentlyStartedRobotStudioPid(requestTimeUtc);
    }

    while (DateTime.UtcNow <= deadline)
    {
      if (pid is not null && TryGetLiveProcessIdentity(pid.Value, out var startUtc, out _))
      {
        _stateStore.SaveTrackedProcess(pid.Value, startUtc);
        return new
        {
          started = true,
          processId = pid.Value,
          startTimeUtc = startUtc,
          exePath = resolvedExePath,
          rsaddinPath = normalizedRsaddinPath,
          stationPath = requestedStationPath,
          arguments = args,
          environment = normalizedEnvironment,
          killSummary,
          message = string.IsNullOrWhiteSpace(args)
              ? "RobotStudio started and is now tracked."
              : "RobotStudio started with requested launch arguments and is now tracked."
        };
      }

      var elapsedFraction = (float)((DateTime.UtcNow - requestTimeUtc).TotalSeconds / timeoutSeconds);
      progress?.Report(new ProgressNotificationValue
      {
        Progress = Math.Clamp(elapsedFraction, 0f, 1f),
        Total = 1f,
        Message = "Waiting for RobotStudio startup confirmation."
      });

      await Task.Delay(250, cancellationToken);
      if (pid is null)
      {
        pid = TryFindRecentlyStartedRobotStudioPid(requestTimeUtc);
      }
    }

    return new
    {
      started = false,
      timeout = true,
      timeoutSeconds,
      exePath = resolvedExePath,
      rsaddinPath = normalizedRsaddinPath,
      stationPath = requestedStationPath,
      arguments = args,
      environment = normalizedEnvironment,
      killSummary,
      message = "RobotStudio launch requested, but startup confirmation timed out."
    };
  }

  [McpServerTool(Destructive = true, Title = "Stop RobotStudio"), Description("Stop the RobotStudio process previously started/tracked by this MCP server.")]
  public async Task<object> StopStartedRobotStudioProcess(
      [Description("Seconds to wait for graceful shutdown before force kill.")] int gracefulTimeoutSeconds = 10,
      [Description("If true, skip graceful close and force-kill immediately to avoid save dialogs.")] bool forceKillOnly = false,
      CancellationToken cancellationToken = default)
  {
    gracefulTimeoutSeconds = Math.Clamp(gracefulTimeoutSeconds, 1, 120);

    if (!_stateStore.TryGetTrackedLiveProcess(out var tracked, out var reason))
    {
      return new
      {
        stopped = false,
        nothingToStop = true,
        message = reason
      };
    }

    var pid = tracked.Id;
    var attemptedGraceful = false;
    var forcedKill = false;

    if (!forceKillOnly)
    {
      try
      {
        if (!tracked.HasExited && tracked.CloseMainWindow())
        {
          attemptedGraceful = true;
          var gracefulMs = gracefulTimeoutSeconds * 1000;
          await tracked.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(gracefulMs), cancellationToken);
        }
      }
      catch (TimeoutException)
      {
      }
      catch
      {
      }
    }

    if (!tracked.HasExited)
    {
      tracked.Kill(entireProcessTree: true);
      // Confirm the kill actually completed even if the caller cancelled, so tracked state below
      // always reflects reality. CancellationToken.None is intentional.
      await tracked.WaitForExitAsync(CancellationToken.None);
      forcedKill = true;
    }

    _stateStore.ClearTrackedProcess("Tracked RobotStudio process was stopped by StopStartedRobotStudioProcess.");

    return new
    {
      stopped = true,
      processId = pid,
      attemptedGraceful,
      forcedKill,
      message = "Tracked RobotStudio process has been stopped and state cleared."
    };
  }

  private static string ResolveRobotStudioExe(string? robotStudioExePath)
  {
    if (!string.IsNullOrWhiteSpace(robotStudioExePath)) return robotStudioExePath;

    var defaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "ABB", "RobotStudio 2026", "Bin", "RobotStudio.exe");

    return File.Exists(defaultPath) ? defaultPath : "RobotStudio";
  }

  private static int? TryFindRecentlyStartedRobotStudioPid(DateTime requestTimeUtc)
  {
    try
    {
      return Process
          .GetProcessesByName("RobotStudio")
          .OrderByDescending(p => p.StartTime)
          .FirstOrDefault(p => p.StartTime.ToUniversalTime() >= requestTimeUtc.AddSeconds(-2))
          ?.Id;
    }
    catch
    {
      return null;
    }
  }

  private static bool TryGetLiveProcessIdentity(int processId, out DateTime startTimeUtc, out string reason)
  {
    try
    {
      var process = Process.GetProcessById(processId);
      if (process.HasExited)
      {
        startTimeUtc = default;
        reason = "Process already exited.";
        return false;
      }

      startTimeUtc = process.StartTime.ToUniversalTime();
      reason = "Process is alive.";
      return true;
    }
    catch (Exception ex)
    {
      startTimeUtc = default;
      reason = ex.Message;
      return false;
    }
  }
}
