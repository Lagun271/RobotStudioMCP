using RobotStudioMCP.AddinServer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using static RobotStudioMCP.ActiveStationStatus;
using static RobotStudioMCP.AddinServer.AddinMcpArgs;
using static RobotStudioMCP.AddinServer.AddinMcpSchema;

namespace RobotStudioMCP
{
  /// <summary>
  /// Tools that ship in every add-in edition: enough for watchdog readiness checks and
  /// station-presence guards, with no station-editing capabilities.
  /// </summary>
  internal static class CoreTools
  {
    public static IEnumerable<AddinMcpTool> CreateTools()
    {
      return new[]
      {
        new AddinMcpTool
        {
          Name = "check_active_station",
          Description = "Checks if a RobotStudio station is active/open and returns structured station status without showing UI.",
          InputSchema = EmptySchema(),
          Handler = CheckActiveStation
        },
        new AddinMcpTool
        {
          Name = "robotstudio_assembly_list",
          Description = "Lists loaded managed assemblies in the RobotStudio process, with assembly/file/product version metadata and glob filtering. When no filters are supplied, returns the RobotStudio MCP add-in assemblies plus a hint to use includeGlob/excludeGlob.",
          InputSchema = BuildSchema(
              new Dictionary<string, object>
              {
                  ["includeGlob"] = StringProperty("Optional glob pattern for assembly names and paths to include. When omitted, RobotStudioMCP assemblies are returned."),
                  ["excludeGlob"] = StringProperty("Optional glob pattern for assembly names and paths to exclude.")
              }),
          Handler = ListRobotStudioAssemblies
        },
      };
    }

    private static AddinMcpToolResult CheckActiveStation(IDictionary<string, object> args)
    {
      if (!TryGetActiveStation(out var station, out var payload))
      {
        return AddinMcpToolResult.Error(payload);
      }

      return AddinMcpToolResult.Success(payload);
    }

    private static AddinMcpToolResult ListRobotStudioAssemblies(IDictionary<string, object> args)
    {
      var includeGlob = (GetString(args, "includeGlob") ?? string.Empty).Trim();
      var excludeGlob = (GetString(args, "excludeGlob") ?? string.Empty).Trim();

      try
      {
        var assemblies = new List<Dictionary<string, object>>();

        if (string.IsNullOrWhiteSpace(includeGlob) && string.IsNullOrWhiteSpace(excludeGlob))
        {
          foreach (var selectedAssembly in GetDefaultAssemblyInspectionAssemblies())
          {
            if (selectedAssembly == null || selectedAssembly.IsDynamic)
            {
              continue;
            }

            var selectedLocation = NormalizePath(SafeAssemblyLocation(selectedAssembly));
            var isAddinAssembly = HasAddinMainEntryPoint(selectedAssembly);
            assemblies.Add(BuildAssemblyInspectionEntry(selectedAssembly, selectedLocation, isAddinAssembly));
          }
        }
        else
        {
          foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
          {
            if (assembly == null || assembly.IsDynamic)
            {
              continue;
            }

            var assemblyLocation = NormalizePath(SafeAssemblyLocation(assembly));
            var assemblyName = assembly.GetName();
            var assemblyFileName = string.IsNullOrWhiteSpace(assemblyLocation) ? null : Path.GetFileName(assemblyLocation);
            var isAddinAssembly = HasAddinMainEntryPoint(assembly);
            var matchesFilters = MatchesInspectionFilters(includeGlob, excludeGlob, assemblyName?.Name, assembly.FullName, assemblyLocation, assemblyFileName);

            if (!matchesFilters)
            {
              continue;
            }

            assemblies.Add(BuildAssemblyInspectionEntry(assembly, assemblyLocation, isAddinAssembly));
          }
        }

        assemblies = assemblies
            .OrderBy(entry => GetDictString(entry, "assemblyName"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => GetDictString(entry, "filePath"), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, object>
        {
          ["ok"] = true,
          ["scope"] = string.IsNullOrWhiteSpace(includeGlob) && string.IsNullOrWhiteSpace(excludeGlob) ? "addin-default" : "filtered",
          ["capturedUtc"] = DateTime.UtcNow.ToString("o"),
          ["includeGlob"] = string.IsNullOrWhiteSpace(includeGlob) ? null : includeGlob,
          ["excludeGlob"] = string.IsNullOrWhiteSpace(excludeGlob) ? null : excludeGlob,
          ["hint"] = string.IsNullOrWhiteSpace(includeGlob) && string.IsNullOrWhiteSpace(excludeGlob)
              ? "Pass includeGlob/excludeGlob to expand or narrow the result set (for example includeGlob=RobotStudioMCP* or excludeGlob=System.*)."
              : null,
          ["totalCount"] = assemblies.Count,
          ["assemblies"] = assemblies.ToArray()
        };

        return AddinMcpToolResult.Success(result);
      }
      catch (Exception ex)
      {
        return AddinMcpToolResult.Error(new Dictionary<string, object>
        {
          ["ok"] = false,
          ["error"] = ex.Message,
          ["tool"] = "robotstudio_assembly_list"
        });
      }
    }

    private static Dictionary<string, object> BuildAssemblyInspectionEntry(
        Assembly assembly,
        string location,
        bool isAddinAssembly)
    {
      var name = assembly?.GetName();
      var versionInfo = SafeFileVersionInfo(location);

      return new Dictionary<string, object>
      {
        ["assemblyName"] = name?.Name,
        ["assemblyFullName"] = assembly?.FullName,
        ["assemblyVersion"] = name?.Version?.ToString(),
        ["filePath"] = location,
        ["fileVersion"] = versionInfo?.FileVersion,
        ["productVersion"] = versionInfo?.ProductVersion,
        ["description"] = versionInfo?.FileDescription,
        ["isDynamic"] = assembly != null && assembly.IsDynamic,
        ["isAddinAssembly"] = isAddinAssembly
      };
    }

    private static IEnumerable<Assembly> GetDefaultAssemblyInspectionAssemblies()
    {
      var currentAssembly = typeof(CoreTools).Assembly;
      var result = new List<Assembly>();
      if (currentAssembly != null)
      {
        result.Add(currentAssembly);
      }

      var explicitRsaddinAssembly = AppDomain.CurrentDomain.GetAssemblies()
          .Where(assembly => assembly != null && !assembly.IsDynamic)
          .Select(assembly => new
          {
            Assembly = assembly,
            Location = NormalizePath(SafeAssemblyLocation(assembly)),
            Name = assembly.GetName()?.Name
          })
          .Where(candidate => candidate.Assembly != null && !ReferenceEquals(candidate.Assembly, currentAssembly))
          .Where(candidate => HasAddinMainEntryPoint(candidate.Assembly))
          .Where(candidate => IsExplicitRsaddinLoadedAssembly(candidate.Location))
          .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
          .ThenBy(candidate => candidate.Location, StringComparer.OrdinalIgnoreCase)
          .Select(candidate => candidate.Assembly)
          .FirstOrDefault();

      if (explicitRsaddinAssembly != null)
      {
        result.Add(explicitRsaddinAssembly);
      }

      return result;
    }

    private static bool IsExplicitRsaddinLoadedAssembly(string location)
    {
      if (string.IsNullOrWhiteSpace(location))
      {
        return false;
      }

      var normalized = location.Trim();
      if (normalized.IndexOf(@"\ABB\RobotStudio 2026\", StringComparison.OrdinalIgnoreCase) >= 0)
      {
        return false;
      }

      return normalized.IndexOf(@"\ABB\DistributionPackages", StringComparison.OrdinalIgnoreCase) >= 0
          || normalized.IndexOf(@"\Add-In\", StringComparison.OrdinalIgnoreCase) >= 0
          || normalized.IndexOf(@"\RobotStudio\Add-In\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool MatchesInspectionFilters(string includeGlob, string excludeGlob, params string[] values)
    {
      var candidates = values == null
          ? Array.Empty<string>()
          : values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();

      if (!string.IsNullOrWhiteSpace(includeGlob) && !candidates.Any(value => MatchesGlob(value, includeGlob)))
      {
        return false;
      }

      if (!string.IsNullOrWhiteSpace(excludeGlob) && candidates.Any(value => MatchesGlob(value, excludeGlob)))
      {
        return false;
      }

      return true;
    }

    private static bool MatchesGlob(string value, string glob)
    {
      if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(glob))
      {
        return false;
      }

      return Regex.IsMatch(value, GlobToRegex(glob), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string GlobToRegex(string glob)
    {
      var builder = new StringBuilder();
      builder.Append('^');

      foreach (var ch in glob ?? string.Empty)
      {
        switch (ch)
        {
          case '*':
            builder.Append(".*");
            break;
          case '?':
            builder.Append('.');
            break;
          default:
            builder.Append(Regex.Escape(ch.ToString()));
            break;
        }
      }

      builder.Append('$');
      return builder.ToString();
    }

    private static bool HasAddinMainEntryPoint(Assembly assembly)
    {
      if (assembly == null || assembly.IsDynamic)
      {
        return false;
      }

      try
      {
        foreach (var type in assembly.GetTypes())
        {
          if (type == null)
          {
            continue;
          }

          var method = type.GetMethod(
              "AddinMain",
              BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
              null,
              Type.EmptyTypes,
              null);
          if (method != null && method.ReturnType == typeof(void))
          {
            return true;
          }
        }
      }
      catch (ReflectionTypeLoadException ex)
      {
        foreach (var type in ex.Types)
        {
          if (type == null)
          {
            continue;
          }

          var method = type.GetMethod(
              "AddinMain",
              BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
              null,
              Type.EmptyTypes,
              null);
          if (method != null && method.ReturnType == typeof(void))
          {
            return true;
          }
        }
      }
      catch
      {
      }

      return false;
    }

    private static string SafeAssemblyLocation(Assembly assembly)
    {
      if (assembly == null)
      {
        return null;
      }

      try
      {
        return assembly.IsDynamic ? null : assembly.Location;
      }
      catch
      {
        return null;
      }
    }

    private static FileVersionInfo SafeFileVersionInfo(string filePath)
    {
      if (string.IsNullOrWhiteSpace(filePath))
      {
        return null;
      }

      try
      {
        return FileVersionInfo.GetVersionInfo(filePath);
      }
      catch
      {
        return null;
      }
    }

    private static string NormalizePath(string path)
    {
      if (string.IsNullOrWhiteSpace(path))
      {
        return null;
      }

      try
      {
        return Path.GetFullPath(path.Trim());
      }
      catch
      {
        return path.Trim();
      }
    }

    private static string GetDictString(IDictionary<string, object> data, string key)
    {
      if (data == null || !data.TryGetValue(key, out var value) || value == null)
      {
        return null;
      }

      return value.ToString();
    }
  }
}
