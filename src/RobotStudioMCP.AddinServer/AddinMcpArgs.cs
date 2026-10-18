using System.Collections.Generic;

namespace RobotStudioMCP.AddinServer
{
  public static class AddinMcpArgs
  {
    public static string GetString(IDictionary<string, object> args, string key)
    {
      if (args == null || !args.TryGetValue(key, out var value) || value == null)
      {
        return null;
      }

      return value.ToString();
    }

    public static int GetIntOrDefault(IDictionary<string, object> args, string key, int defaultValue)
        => GetNormalizedInt(args, key) ?? defaultValue;

    public static int? GetNormalizedInt(IDictionary<string, object> args, string key)
    {
      if (args == null || !args.TryGetValue(key, out var value) || value == null)
      {
        return null;
      }

      return value is int i ? i : (int?)null;
    }

    public static bool? GetNormalizedBool(IDictionary<string, object> args, string key)
    {
      if (args == null || !args.TryGetValue(key, out var value) || value == null)
      {
        return null;
      }

      return value is bool b ? b : (bool?)null;
    }
  }
}
