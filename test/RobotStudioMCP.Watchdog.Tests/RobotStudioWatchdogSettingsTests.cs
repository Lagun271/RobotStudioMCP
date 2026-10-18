using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RobotStudioMCP.Watchdog.Tests;

[TestClass]
public sealed class RobotStudioWatchdogSettingsTests
{
  [TestMethod]
  public void LoadFromDefaultLocations_WhenMissing_ReturnsDefaults()
  {
    var originalCwd = Environment.CurrentDirectory;
    var tempDir = Path.Combine(Path.GetTempPath(), "watchdog-settings-tests-" + Guid.NewGuid());
    Directory.CreateDirectory(tempDir);

    try
    {
      Environment.CurrentDirectory = tempDir;
      RobotStudioWatchdogSettings.LoadFromDefaultLocations();

      Assert.IsNotNull(RobotStudioWatchdogSettings.Current);
      Assert.AreEqual(5000, RobotStudioWatchdogSettings.Current.StartupCooldownAfterKillMs);
    }
    finally
    {
      Environment.CurrentDirectory = originalCwd;
      RobotStudioWatchdogSettings.LoadFromDefaultLocations();
      try { Directory.Delete(tempDir, recursive: true); } catch { }
    }
  }

  [TestMethod]
  public void LoadFromDefaultLocations_WhenInvalid_Throws()
  {
    var originalCwd = Environment.CurrentDirectory;
    var tempDir = Path.Combine(Path.GetTempPath(), "watchdog-settings-tests-" + Guid.NewGuid());
    Directory.CreateDirectory(tempDir);

    var settingsPath = Path.Combine(tempDir, RobotStudioWatchdogSettings.FileName);
    File.WriteAllText(settingsPath, "{ \"StartupCooldownAfterKillMs\": -5 }");

    try
    {
      Environment.CurrentDirectory = tempDir;
      var ex = Assert.ThrowsException<InvalidOperationException>(() => RobotStudioWatchdogSettings.LoadFromDefaultLocations());
      StringAssert.Contains(ex.Message, "StartupCooldownAfterKillMs");
    }
    finally
    {
      Environment.CurrentDirectory = originalCwd;
      RobotStudioWatchdogSettings.LoadFromDefaultLocations();
      try { Directory.Delete(tempDir, recursive: true); } catch { }
    }
  }

  [TestMethod]
  public void LoadFromDefaultLocations_WhenValid_OverridesDefaults()
  {
    var originalCwd = Environment.CurrentDirectory;
    var tempDir = Path.Combine(Path.GetTempPath(), "watchdog-settings-tests-" + Guid.NewGuid());
    Directory.CreateDirectory(tempDir);

    var settingsPath = Path.Combine(tempDir, RobotStudioWatchdogSettings.FileName);
    File.WriteAllText(settingsPath, "{ \"StartupCooldownAfterKillMs\": 1200 }");

    try
    {
      Environment.CurrentDirectory = tempDir;
      RobotStudioWatchdogSettings.LoadFromDefaultLocations();
      Assert.AreEqual(1200, RobotStudioWatchdogSettings.Current.StartupCooldownAfterKillMs);
    }
    finally
    {
      Environment.CurrentDirectory = originalCwd;
      RobotStudioWatchdogSettings.LoadFromDefaultLocations();
      try { Directory.Delete(tempDir, recursive: true); } catch { }
    }
  }
}
