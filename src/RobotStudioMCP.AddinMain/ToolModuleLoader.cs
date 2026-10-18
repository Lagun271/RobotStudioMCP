using RobotStudioMCP.AddinServer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RobotStudioMCP
{
  /// <summary>
  /// Loads optional tool modules (<c>RobotStudioMCP.Tools.*.dll</c>) from the add-in directory.
  /// An absent module is not an error (core edition). A module that is present but fails to load,
  /// or that redefines an existing tool name, is reported in the module status and contributes no tools.
  /// </summary>
  internal static class ToolModuleLoader
  {
    internal const string ModuleFilePattern = "RobotStudioMCP.Tools.*.dll";

    internal sealed class LoadResult
    {
      public IList<AddinMcpTool> Tools { get; } = new List<AddinMcpTool>();
      public IList<IDictionary<string, object>> Modules { get; } = new List<IDictionary<string, object>>();
    }

    internal static LoadResult Load(string addinDirectory, IEnumerable<string> reservedToolNames)
    {
      var result = new LoadResult();
      var knownNames = new HashSet<string>(reservedToolNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

      if (string.IsNullOrWhiteSpace(addinDirectory) || !Directory.Exists(addinDirectory))
      {
        return result;
      }

      foreach (var file in Directory.GetFiles(addinDirectory, ModuleFilePattern).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
      {
        var status = new Dictionary<string, object>
        {
          ["file"] = Path.GetFileName(file),
          ["loaded"] = false,
          ["toolCount"] = 0
        };
        result.Modules.Add(status);

        try
        {
          var assembly = Assembly.LoadFrom(file);
          var moduleTypes = assembly.GetTypes()
              .Where(type => typeof(IAddinToolModule).IsAssignableFrom(type) && type.IsClass && !type.IsAbstract)
              .ToArray();
          if (moduleTypes.Length == 0)
          {
            status["error"] = $"No public {nameof(IAddinToolModule)} implementation found.";
            continue;
          }

          var moduleTools = new List<AddinMcpTool>();
          var moduleNames = new List<string>();
          foreach (var moduleType in moduleTypes)
          {
            var module = (IAddinToolModule)Activator.CreateInstance(moduleType);
            moduleNames.Add(module.Name);
            moduleTools.AddRange(module.CreateTools() ?? Enumerable.Empty<AddinMcpTool>());
          }

          status["name"] = string.Join(",", moduleNames);

          var duplicates = moduleTools
              .Select(tool => tool?.Name)
              .Where(name => !string.IsNullOrWhiteSpace(name))
              .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
              .Where(group => group.Count() > 1 || knownNames.Contains(group.Key))
              .Select(group => group.Key)
              .ToArray();
          if (duplicates.Length > 0)
          {
            status["error"] = "Module redefines existing tool names: " + string.Join(", ", duplicates);
            continue;
          }

          foreach (var tool in moduleTools)
          {
            knownNames.Add(tool.Name);
            result.Tools.Add(tool);
          }

          status["loaded"] = true;
          status["toolCount"] = moduleTools.Count;
        }
        catch (ReflectionTypeLoadException ex)
        {
          var loaderMessages = ex.LoaderExceptions?.Where(e => e != null).Select(e => e.Message).Distinct().ToArray() ?? Array.Empty<string>();
          status["error"] = ex.Message + (loaderMessages.Length > 0 ? " " + string.Join(" | ", loaderMessages) : string.Empty);
        }
        catch (Exception ex)
        {
          status["error"] = (ex.InnerException ?? ex).Message;
        }
      }

      return result;
    }
  }
}
