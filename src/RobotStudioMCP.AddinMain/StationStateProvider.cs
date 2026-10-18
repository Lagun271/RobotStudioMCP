using ABB.Robotics.RobotStudio.Stations;
using RobotStudioMCP.Core.Station;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RobotStudioMCP
{
  internal static class RobotStudioStationStateProvider
  {
    internal static StationState CaptureCurrentStationState()
    {
      return CaptureStationState(Station.ActiveStation);
    }

    internal static StationState CaptureStationState(Station station)
    {
      var controllers = EnumerateControllerState(station).ToArray();
      return StationState.Create(station != null, controllers);
    }

    internal static IDictionary<string, object> ToPayload(StationState stationState)
    {
      return new Dictionary<string, object>
      {
        ["hasActiveStation"] = stationState?.HasActiveStation ?? false,
        ["controllerCount"] = stationState?.ControllerCount ?? 0,
        ["allControllersReady"] = stationState?.AllControllersReady ?? false,
        ["controllers"] = (stationState?.Controllers ?? Array.Empty<ControllerState>())
            .Select(controller => new Dictionary<string, object>
            {
              ["name"] = controller.Name,
              ["systemState"] = controller.SystemState,
              ["isReady"] = controller.IsReady
            })
            .ToArray()
      };
    }

    private static IEnumerable<ControllerState> EnumerateControllerState(Station station)
    {
      if (station?.VirtualControllers == null)
      {
        yield break;
      }

      foreach (var controller in station.VirtualControllers.Cast<object>().OfType<VirtualController>())
      {
        yield return new ControllerState
        {
          Name = controller.Name ?? string.Empty,
          SystemState = Convert.ToString(controller.SystemState) ?? string.Empty
        };
      }
    }
  }
}
