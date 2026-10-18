using ABB.Robotics.RobotStudio.Stations;
using System.Collections.Generic;

namespace RobotStudioMCP
{
  /// <summary>
  /// Shared active-station guard used by core tools and tool modules.
  /// </summary>
  public static class ActiveStationStatus
  {
    public static bool TryGetActiveStation(out Station station, out IDictionary<string, object> payload)
    {
      station = Station.ActiveStation;
      if (station != null)
      {
        payload = BuildStationPayload(station, true, null);
        return true;
      }

      var message = "No active station is open. Please open a project/station in RobotStudio, then retry.";
      payload = BuildStationPayload(null, false, message);
      return false;
    }

    public static IDictionary<string, object> BuildStationPayload(Station station, bool isActive, string error)
    {
      var stationState = RobotStudioStationStateProvider.CaptureStationState(station);

      var data = new Dictionary<string, object>
      {
        ["ok"] = isActive,
        ["isActive"] = isActive,
        ["stationName"] = station?.Name,
        ["stationDisplayName"] = station?.DisplayName,
        ["stationPath"] = station?.FileInfo?.FullName,
        ["stationState"] = RobotStudioStationStateProvider.ToPayload(stationState)
      };

      if (!string.IsNullOrWhiteSpace(error))
      {
        data["error"] = error;
      }

      return data;
    }
  }
}
