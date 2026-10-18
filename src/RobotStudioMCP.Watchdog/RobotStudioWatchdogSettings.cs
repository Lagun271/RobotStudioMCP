using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobotStudioMCP.Watchdog;

internal sealed class RobotStudioWatchdogSettings
{
  public const string FileName = "robotstudio-watchdog.settings.json";
  private const int DefaultStartupCooldownAfterKillMs = 5000;
  private const int MinStartupCooldownAfterKillMs = 0;
  private const int MaxStartupCooldownAfterKillMs = 120000;

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
  };

  public int StartupCooldownAfterKillMs { get; init; } = DefaultStartupCooldownAfterKillMs;

  public static RobotStudioWatchdogSettings Current { get; private set; } = new();

  public static void LoadFromDefaultLocations()
  {
    var settingsPath = ResolveSettingsPath();
    if (settingsPath is null)
    {
      Current = new RobotStudioWatchdogSettings();
      return;
    }

    Current = LoadFromFile(settingsPath);
  }

  private static string? ResolveSettingsPath()
  {
    var candidates = new[]
    {
      Path.Combine(AppContext.BaseDirectory, FileName),
      Path.Combine(Environment.CurrentDirectory, FileName)
    };

    return candidates.FirstOrDefault(File.Exists);
  }

  private static RobotStudioWatchdogSettings LoadFromFile(string path)
  {
    try
    {
      var json = File.ReadAllText(path);
      var parsed = JsonSerializer.Deserialize<RobotStudioWatchdogSettings>(json, JsonOptions)
          ?? throw new InvalidOperationException($"Settings file '{path}' did not deserialize to an object.");

      if (parsed.StartupCooldownAfterKillMs < MinStartupCooldownAfterKillMs || parsed.StartupCooldownAfterKillMs > MaxStartupCooldownAfterKillMs)
      {
        throw new InvalidOperationException(
            $"Invalid StartupCooldownAfterKillMs={parsed.StartupCooldownAfterKillMs} in '{path}'. " +
            $"Expected {MinStartupCooldownAfterKillMs}..{MaxStartupCooldownAfterKillMs}.");
      }

      return parsed;
    }
    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
    {
      throw new InvalidOperationException($"Failed to load watchdog settings from '{path}': {ex.Message}", ex);
    }
  }
}
