using RobotStudioMCP.AddinServer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RobotStudioMCP
{
  public class AddIn
  {
    private static AddinPipeServer _server;

    // This is the entry point which will be called when the Add-in is loaded
    public static void AddinMain()
    {
      if (_server != null)
      {
        return;
      }

      var options = new AddinMcpServerOptions
      {
        ServerName = "RobotStudioMCP.Addin",
        ServerVersion = GetServerVersion()
      };

      var coreTools = CoreTools.CreateTools().ToArray();
      var addinDirectory = Path.GetDirectoryName(typeof(AddIn).Assembly.Location);
      var modules = ToolModuleLoader.Load(
          addinDirectory,
          coreTools.Select(tool => tool.Name).Concat(new[] { "get_robotstudio_status" }));

      _server = new AddinPipeServer(
          options,
          GetPipeName(),
          () => new Dictionary<string, object>
          {
            ["addinLoaded"] = true,
            ["processName"] = System.Diagnostics.Process.GetCurrentProcess().ProcessName,
            ["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id,
            ["modules"] = modules.Modules.ToArray()
          },
          coreTools.Concat(modules.Tools));

      _server.Start();
    }

    // Suffixed by this process's own id so multiple RobotStudio instances on the same machine never
    // collide on one pipe name; the watchdog derives the same name from the RobotStudio process it's
    // tracking. Overridable for the rare case a caller needs a fixed, predictable name instead.
    internal static string GetPipeName()
    {
      var env = Environment.GetEnvironmentVariable("ROBOTSTUDIOMCP_ADDIN_PIPE_NAME");
      return !string.IsNullOrWhiteSpace(env)
          ? env
          : PipeNameForProcess(System.Diagnostics.Process.GetCurrentProcess().Id);
    }

    public static string PipeNameForProcess(int processId) => $"RobotStudioMCP-Addin-{processId}";

    // GitVersion drives $(FileVersion)/$(InformationalVersion) at build time, embedded in the native
    // Win32 version resource regardless of the repo-wide AssemblyInfo-attribute generation settings
    // (see Directory.Build.props) — reading it back this way keeps this in sync without duplicating
    // the version anywhere by hand. Falls back to the assembly's own version if unavailable (e.g. a
    // local build outside the normal GitVersion-driven pipeline).
    private static string GetServerVersion()
    {
      try
      {
        var location = typeof(AddIn).Assembly.Location;
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
        // Fall through to the assembly version fallback below.
      }

      return typeof(AddIn).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
  }
}
