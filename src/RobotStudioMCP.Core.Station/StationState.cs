using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RobotStudioMCP.Core.Station;

public sealed record StationState
{
  public bool HasActiveStation { get; init; }
  public int ControllerCount { get; init; }
  public IReadOnlyList<ControllerState> Controllers { get; init; } = Array.Empty<ControllerState>();
  public bool AllControllersReady { get; init; }

  public static StationState Create(bool hasActiveStation, IEnumerable<ControllerState>? controllers)
  {
    var controllerList = controllers?.ToArray() ?? Array.Empty<ControllerState>();

    return new StationState
    {
      HasActiveStation = hasActiveStation,
      ControllerCount = controllerList.Length,
      Controllers = controllerList,
      AllControllersReady = hasActiveStation && (controllerList.Length == 0 || controllerList.All(controller => controller.IsReady))
    };
  }
}

public sealed record ControllerState
{
  private enum ReadySystemState
  {
    Ready,
    Started
  }

  private static readonly ReadySystemState[] ReadySystemStates = [ReadySystemState.Ready, ReadySystemState.Started];

  public string Name { get; init; } = string.Empty;
  public string SystemState { get; init; } = string.Empty;

  public bool IsReady => ReadySystemStates.Any(state => string.Equals(SystemState, state.ToString(), StringComparison.OrdinalIgnoreCase));
}

public interface IStationStateProvider
{
  StationState GetStationState();
}

public static class StationStateProviderExtensions
{
  public static Task<StationState> GetStationStateAsync(
      this IStationStateProvider provider,
      CancellationToken cancellationToken = default)
  {
    if (provider == null)
    {
      throw new ArgumentNullException(nameof(provider));
    }

    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult(provider.GetStationState());
  }

  public static async Task<StationState> WaitForStationStateAsync(
      this IStationStateProvider provider,
      TimeSpan timeout,
      TimeSpan pollInterval,
      CancellationToken cancellationToken = default)
  {
    if (provider == null)
    {
      throw new ArgumentNullException(nameof(provider));
    }

    if (timeout < TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    if (pollInterval <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    var deadline = DateTime.UtcNow + timeout;
    var latest = provider.GetStationState();

    while (DateTime.UtcNow <= deadline)
    {
      cancellationToken.ThrowIfCancellationRequested();
      latest = await provider.GetStationStateAsync(cancellationToken).ConfigureAwait(false);
      if (latest.HasActiveStation && latest.AllControllersReady)
      {
        return latest;
      }

      var remaining = deadline - DateTime.UtcNow;
      if (remaining <= TimeSpan.Zero)
      {
        break;
      }

      var delay = pollInterval < remaining ? pollInterval : remaining;
      await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    return latest;
  }
}
