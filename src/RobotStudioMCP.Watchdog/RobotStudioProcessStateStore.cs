using System.Diagnostics;
using System.Text.Json;

namespace RobotStudioMCP.Watchdog;

public interface IRobotStudioProcessStateStore
{
  ProcessTrackingState Current { get; }
  bool TryGetTrackedLiveProcess(out Process process, out string reason);
  void SaveTrackedProcess(int processId, DateTime processStartTimeUtc);
  void ClearTrackedProcess(string reason);
}

public sealed class ProcessTrackingState
{
  public int? ProcessId { get; init; }
  public DateTime? ProcessStartTimeUtc { get; init; }
  public DateTime? RecordedAtUtc { get; init; }
  public bool StaleStateClearedOnStartup { get; init; }
  public string? StartupReconciliationMessage { get; init; }
}

internal sealed class PersistedProcessState
{
  public int ProcessId { get; set; }
  public DateTime ProcessStartTimeUtc { get; set; }
  public DateTime RecordedAtUtc { get; set; }
}

public sealed class RobotStudioProcessStateStore : IRobotStudioProcessStateStore
{
  private static readonly JsonSerializerOptions PersistJsonOptions = new() { WriteIndented = true };

  private readonly object _gate = new();
  private readonly string _statePath;

  private PersistedProcessState? _state;
  private bool _staleStateClearedOnStartup;
  private string? _startupReconciliationMessage;

  public RobotStudioProcessStateStore()
  {
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobotStudioMCP");
    Directory.CreateDirectory(dir);
    _statePath = Path.Combine(dir, "watchdog-state.json");

    LoadAndReconcile();
  }

  public ProcessTrackingState Current
  {
    get
    {
      lock (_gate)
      {
        return new ProcessTrackingState
        {
          ProcessId = _state?.ProcessId,
          ProcessStartTimeUtc = _state?.ProcessStartTimeUtc,
          RecordedAtUtc = _state?.RecordedAtUtc,
          StaleStateClearedOnStartup = _staleStateClearedOnStartup,
          StartupReconciliationMessage = _startupReconciliationMessage
        };
      }
    }
  }

  public bool TryGetTrackedLiveProcess(out Process process, out string reason)
  {
    lock (_gate)
    {
      if (_state is null)
      {
        process = null!;
        reason = "No tracked RobotStudio process.";
        return false;
      }

      if (!TryGetMatchingProcess(_state, out process, out reason))
      {
        ClearTrackedProcessInternal($"Tracked state became stale: {reason}");
        return false;
      }

      reason = "Tracked process is running.";
      return true;
    }
  }

  public void SaveTrackedProcess(int processId, DateTime processStartTimeUtc)
  {
    lock (_gate)
    {
      _state = new PersistedProcessState
      {
        ProcessId = processId,
        ProcessStartTimeUtc = processStartTimeUtc.ToUniversalTime(),
        RecordedAtUtc = DateTime.UtcNow
      };
      PersistUnsafe();
    }
  }

  public void ClearTrackedProcess(string reason)
  {
    lock (_gate)
    {
      ClearTrackedProcessInternal(reason);
    }
  }

  private void LoadAndReconcile()
  {
    lock (_gate)
    {
      if (!File.Exists(_statePath))
      {
        _startupReconciliationMessage = "No persisted watchdog state found.";
        return;
      }

      try
      {
        var json = File.ReadAllText(_statePath);
        _state = JsonSerializer.Deserialize<PersistedProcessState>(json);
        if (_state is null)
        {
          _startupReconciliationMessage = "Persisted watchdog state was empty. Cleared.";
          _staleStateClearedOnStartup = true;
          DeleteStateFileUnsafe();
          return;
        }

        if (!TryGetMatchingProcess(_state, out _, out var reason))
        {
          _startupReconciliationMessage = $"Cleared stale persisted state on startup: {reason}";
          _staleStateClearedOnStartup = true;
          ClearTrackedProcessInternal(_startupReconciliationMessage);
          return;
        }

        _startupReconciliationMessage = "Recovered tracked RobotStudio process from persisted state.";
      }
      catch (Exception ex)
      {
        _startupReconciliationMessage = $"Failed to parse persisted state ({ex.Message}). Cleared.";
        _staleStateClearedOnStartup = true;
        _state = null;
        DeleteStateFileUnsafe();
      }
    }
  }

  private static bool TryGetMatchingProcess(PersistedProcessState state, out Process process, out string reason)
  {
    try
    {
      process = Process.GetProcessById(state.ProcessId);
    }
    catch (ArgumentException)
    {
      process = null!;
      reason = $"PID {state.ProcessId} does not exist.";
      return false;
    }
    catch (Exception ex)
    {
      process = null!;
      reason = $"Unable to read PID {state.ProcessId}: {ex.Message}";
      return false;
    }

    try
    {
      if (process.HasExited)
      {
        reason = $"PID {state.ProcessId} has already exited.";
        return false;
      }

      var actualStartUtc = process.StartTime.ToUniversalTime();
      if (actualStartUtc != state.ProcessStartTimeUtc)
      {
        reason = $"PID {state.ProcessId} start time mismatch (PID reuse).";
        return false;
      }

      reason = "Process identity matches.";
      return true;
    }
    catch (Exception ex)
    {
      reason = $"Failed reading process details for PID {state.ProcessId}: {ex.Message}";
      return false;
    }
  }

  private void ClearTrackedProcessInternal(string reason)
  {
    _state = null;
    _startupReconciliationMessage = reason;
    DeleteStateFileUnsafe();
  }

  private void PersistUnsafe()
  {
    var json = JsonSerializer.Serialize(_state, PersistJsonOptions);
    File.WriteAllText(_statePath, json);
  }

  private void DeleteStateFileUnsafe()
  {
    if (File.Exists(_statePath))
    {
      File.Delete(_statePath);
    }
  }
}
