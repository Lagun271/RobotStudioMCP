using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobotStudioMCP.Watchdog;

namespace RobotStudioMCP.Watchdog.Tests;

[TestClass]
public class RobotStudioHealthToolsTests
{
  private static RobotStudioHealthTools CreateSut(ProcessTrackingState? tracked = null) =>
      new(new FakeStateStore(tracked ?? new ProcessTrackingState()));

  [TestMethod]
  public void GetRobotStudioProcessStatus_IncludesTrackedState()
  {
    var tracked = new ProcessTrackingState
    {
      ProcessId = 123,
      ProcessStartTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
      RecordedAtUtc = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc),
      StaleStateClearedOnStartup = false,
      StartupReconciliationMessage = "ok"
    };

    var sut = CreateSut(tracked);

    var json = JsonSerializer.SerializeToNode(sut.GetRobotStudioProcessStatus())!;

    Assert.AreEqual(123, (int)json["trackedProcess"]!["ProcessId"]!);
  }

  [TestMethod]
  public async Task CheckAddinEndpoint_Unreachable_ReturnsReachableFalse()
  {
    var sut = CreateSut();
    var result = await sut.CheckAddinEndpoint("RobotStudioMCP-Addin-Tests-NoSuchPipe");
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["reachable"]!);
    Assert.IsNotNull(json["error"]);
  }

  [TestMethod]
  public async Task GetActiveStationStatus_UnreachablePipe_ReturnsOkFalse()
  {
    var sut = CreateSut();
    var result = await sut.GetActiveStationStatus("RobotStudioMCP-Addin-Tests-NoSuchPipe");
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["ok"]!);
    Assert.IsNotNull(json["error"]);
  }

  [TestMethod]
  public async Task CheckAddinEndpoint_NoTrackedProcessAndNoOverride_ReturnsReachableFalse()
  {
    var sut = CreateSut();
    var result = await sut.CheckAddinEndpoint();
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["reachable"]!);
    StringAssert.Contains((string)json["error"]!, "No tracked RobotStudio process");
  }

  [TestMethod]
  public async Task GetActiveStationStatus_NoTrackedProcessAndNoOverride_ReturnsOkFalse()
  {
    var sut = CreateSut();
    var result = await sut.GetActiveStationStatus();
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["ok"]!);
    StringAssert.Contains((string)json["error"]!, "No tracked RobotStudio process");
  }

  [TestMethod]
  public async Task VerifyAddinMcpReady_NoTrackedProcessAndNoOverride_RejectedWithoutRequest()
  {
    var sut = CreateSut();

    var result = await sut.VerifyAddinMcpReady(timeoutSeconds: 1, retryDelayMs: 100);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["ready"]!);
    StringAssert.Contains((string)json["error"]!, "No tracked RobotStudio process");
  }

  [TestMethod]
  public async Task VerifyAddinMcpReady_UnreachablePipe_ReturnsReadyFalse()
  {
    var sut = CreateSut();

    var result = await sut.VerifyAddinMcpReady("RobotStudioMCP-Addin-Tests-NoSuchPipe", timeoutSeconds: 1, retryDelayMs: 100);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["ready"]!);
    Assert.AreEqual("ADDIN_NOT_REQUESTED", (string)json["runtimeReadiness"]!["rsaddinLoad"]!["code"]!);
    Assert.IsNull(json["runtimeReadiness"]!["rsaddinId"]);

    Assert.IsNotNull(json["mcpPackagePaths"]);
    Assert.IsNotNull(json["runtimeReadiness"]!["addinPing"]);
    Assert.IsNotNull(json["runtimeReadiness"]!["mcpAddinLoad"]!["code"]);
    Assert.IsNotNull(json["robotStudioLogs"]);
    Assert.IsNotNull(json["stageDiagnostics"]!["license"]);
  }

  [TestMethod]
  public async Task VerifyAddinMcpReady_DoesNotReturnRsaddinPreflightDiagnostics()
  {
    var sut = CreateSut();

    var result = await sut.VerifyAddinMcpReady(
        "http://127.0.0.1:1",
        timeoutSeconds: 1,
        retryDelayMs: 100);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["ready"]!);
    Assert.IsNotNull(json["stageDiagnostics"]);
    Assert.IsNull(json["stageDiagnostics"]!["rsaddinPreflight"]);
  }

  [TestMethod]
  public async Task VerifyAddinMcpReady_WithLaterLoadedAfterEntrypointException_PreservesInitialFailure()
  {
    using var logs = new LogDirectoriesScope("EntrypointException");

    logs.WriteUsageLog("RobotStudio-20260406-190054.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Premium
----HEADER END----
----LOG BEGIN----
00:00:01|AddinLoading|RobotStudioRosBridge
00:00:02|AddinLoaded|RobotStudioRosBridge
----LOG END----
""");

    logs.WriteErrorLog("RobotStudio-20260406-190054.rslog", """
2026-04-06 19:00:54
00:00:01|UserLogMessage|Error||An exception was thrown when invoking the AddIn entrypoint.
RobotStudio .NET exception: Exception has been thrown by the target of an invocation.
============================================
Base exception: TargetInvocationException
Exception has been thrown by the target of an invocation.
   at System.Reflection.RuntimeMethodInfo.Invoke(Object obj, BindingFlags invokeAttr, Binder binder, Object[] parameters, CultureInfo culture)
   at RobotStudio.ExtensionManagement.Addin.TryLoad(String[] args)
Inner exception: InvalidOperationException
Missing or invalid ROBOTSTUDIO_DEMO_MODE. Expected 'Groove welding' or 'Sheet-metal seam detection'.
   at RobotStudioRosBridge.AddIn.ReadRequiredDemoModeFromEnv() in C:\\Users\\niklas.skoglund\\source\\repos\\path-from-scan\\robotstudio\\src\\RobotStudioRosBridge.AddinMain\\AddInMain.cs:line 993
   at RobotStudioRosBridge.AddIn.AddinMain() in C:\\Users\\niklas.skoglund\\source\\repos\\path-from-scan\\robotstudio\\src\\RobotStudioRosBridge.AddinMain\\AddInMain.cs:line 46
""");

    var sut = CreateSut();

    var result = await sut.VerifyAddinMcpReady(
        "http://127.0.0.1:1",
        timeoutSeconds: 1,
        retryDelayMs: 100,
        rsaddinPath: @"C:\\AnyVendor\\RobotStudioRosBridge.rsaddin",
        usageLogsRoot: logs.UsageRoot,
        errorLogsRoot: logs.ErrorRoot);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["ready"]!);
    Assert.AreEqual("loaded", (string)json["runtimeReadiness"]!["rsaddinLoad"]!["status"]!);
    Assert.IsTrue((bool)json["runtimeReadiness"]!["rsaddinLoad"]!["hadStartupException"]!);
    StringAssert.Contains((string)json["runtimeReadiness"]!["rsaddinLoad"]!["firstAddinEntrypointException"]!, "An exception was thrown when invoking the AddIn entrypoint.");
    StringAssert.Contains((string)json["runtimeReadiness"]!["rsaddinLoad"]!["startupFailureReason"]!, "Missing or invalid ROBOTSTUDIO_DEMO_MODE");
    var evidence = json["runtimeReadiness"]!["rsaddinLoad"]!["evidence"]!.AsArray();
    Assert.IsTrue(evidence.Any(item => item!.GetValue<string>().Contains("An exception was thrown when invoking the AddIn entrypoint.")));

    Assert.AreEqual("addin_load", (string)json["stageDiagnostics"]!["stage"]!);
    Assert.AreEqual("robotstudio_addin_load", (string)json["stageDiagnostics"]!["lastFailureType"]!);
    Assert.IsNotNull(json["stageDiagnostics"]!["mcpAddinLoad"]!["firstAddinEntrypointException"]);
    Assert.AreEqual("a definitive add-in load outcome", (string)json["stageDiagnostics"]!["progress"]!["waitingFor"]!);
  }

  [TestMethod]
  public async Task VerifyAddinMcpReady_WithMcpLoadFailureFromLogs_ReportsFailedStatusAndReason()
  {
    using var logs = new LogDirectoriesScope("McpFailure");

    logs.WriteUsageLog("RobotStudio-20260406-190054.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Premium
----HEADER END----
----LOG BEGIN----
00:00:01|AddinLoading|RobotStudioMCP
----LOG END----
""");

    logs.WriteErrorLog("RobotStudio-20260406-190054.rslog", """
2026-04-06 19:00:54
00:00:02|Error|Failed to load RobotStudioMCP.rsaddin
""");

    var sut = CreateSut();

    var result = await sut.VerifyAddinMcpReady(
        "http://127.0.0.1:1",
        timeoutSeconds: 1,
        retryDelayMs: 100,
        usageLogsRoot: logs.UsageRoot,
        errorLogsRoot: logs.ErrorRoot);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["ready"]!);
    Assert.AreEqual("failed", (string)json["runtimeReadiness"]!["mcpAddinLoad"]!["status"]!);
    Assert.IsFalse((bool)json["runtimeReadiness"]!["mcpAddinLoad"]!["hadStartupException"]!);
    StringAssert.Contains((string)json["runtimeReadiness"]!["mcpAddinLoad"]!["startupFailureReason"]!, "Failed to load RobotStudioMCP.rsaddin");
    var evidence = json["runtimeReadiness"]!["mcpAddinLoad"]!["evidence"]!.AsArray();
    Assert.IsTrue(evidence.Any(item => item!.GetValue<string>().Contains("Failed to load RobotStudioMCP.rsaddin")));

    Assert.AreEqual("addin_load", (string)json["stageDiagnostics"]!["stage"]!);
    Assert.AreEqual("robotstudio_addin_load", (string)json["stageDiagnostics"]!["lastFailureType"]!);
  }

  [TestMethod]
  public async Task VerifyAddinMcpReady_UsesConfiguredProbeTimeoutInPayload()
  {
    var sut = CreateSut();

    var result = await sut.VerifyAddinMcpReady(
        "http://127.0.0.1:1",
        timeoutSeconds: 1,
        retryDelayMs: 100,
        probeTimeoutMs: 200);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.AreEqual(200, (int)json["effectiveTimeouts"]!["probeTimeoutMs"]!);
  }

  [TestMethod]
  public void VerifyAddinInstallation_WhenMatchingRspakLayoutExists_ReturnsInstalledTrue()
  {
    using var temp = new TempDirectoryScope("InstallTest");
    temp.CreateDirectory("RobotStudioMCP-1.0.0/RobotStudio");

    var result = RobotStudioHealthTools.VerifyAddinInstallation(
        packageId: "RobotStudioMCP",
        distributionPackagesRoot: temp.DirectoryPath,
        distributionPackagesRoot2: temp.PathTo("missing"));
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsTrue((bool)json["installed"]!);
    Assert.AreEqual("rspak", (string)json["method"]!);
  }

  [TestMethod]
  public void EvaluateLicenseReadinessForAddinLoad_WhenLicenseLevelMissing_ReturnsNotReady()
  {
    var diagnostics = new RobotStudioLicenseDiagnostics
    {
      Found = true,
      LicenseLevel = null
    };

    var result = RobotStudioHealthTools.EvaluateLicenseReadinessForAddinLoad(diagnostics);

    Assert.IsFalse(result.ok);
    StringAssert.Contains(result.reason, "license level");
  }

  [TestMethod]
  public void EvaluateLicenseReadinessForAddinLoad_WhenLicenseLevelIsFree_ReturnsNotReady()
  {
    var diagnostics = new RobotStudioLicenseDiagnostics
    {
      Found = true,
      LicenseLevel = "Free"
    };

    var result = RobotStudioHealthTools.EvaluateLicenseReadinessForAddinLoad(diagnostics);

    Assert.IsFalse(result.ok);
    StringAssert.Contains(result.reason, "Free");
  }

  [TestMethod]
  public void ParseLatestUsageLogLicenseInfo_ReadsLicenseFieldsFromHeader()
  {
    using var temp = new TempDirectoryScope("UsageLog");
    var logPath = temp.CreateFile("RobotStudio-20260329-212927.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
SessionStartUtc=03/29/2026 14:29:26
AppVersion=25.5.11605.0
Licenses=Floating;Partner Edition
LicenseFeatures=Premium|ArcWeld_PowerPac
LicenseLevel=Premium
PremiumLicenseType=Floating
----HEADER END----
----LOG BEGIN----
00:00:02.2898427|AddinAdded|RobotStudioMCP|General
----LOG END----
""");

    var result = RobotStudioHealthTools.ParseLatestUsageLogLicenseInfo(temp.DirectoryPath);

    Assert.IsTrue(result.Found);
    Assert.AreEqual(logPath, result.LogPath);
    Assert.AreEqual("Premium", result.LicenseLevel);
    Assert.AreEqual("Floating;Partner Edition", result.Licenses);
    Assert.AreEqual("Premium|ArcWeld_PowerPac", result.LicenseFeatures);
    Assert.AreEqual("Floating", result.PremiumLicenseType);
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_FindsLoadProgressAndFailureEntries()
  {
    using var logs = new LogDirectoriesScope("Entries");

    logs.WriteUsageLog("usage.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Premium
----HEADER END----
----LOG BEGIN----
00:00:01|AddinAdded|RobotStudioMCP|General
00:00:02|AddinLoaded|RobotStudioMCP
----LOG END----
""");

    logs.WriteErrorLog("error.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Premium
----HEADER END----
----LOG BEGIN----
00:00:03|Error|Failed to load RobotStudioMCP.rsaddin
----LOG END----
""");

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles("RobotStudioMCP", logs.UsageRoot, logs.ErrorRoot, maxFilesPerKind: 2);

    Assert.IsTrue(result.LoadProgressEntries.Any(entry => entry.Contains("AddinAdded|RobotStudioMCP", StringComparison.Ordinal)));
    Assert.IsTrue(result.LoadFailureEntries.Any(entry => entry.Contains("Failed to load RobotStudioMCP.rsaddin", StringComparison.Ordinal)));
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_FlagsGenericLoaderExceptionsAsLoadFailure()
  {
    using var logs = new LogDirectoriesScope("GenericLoader");

    logs.WriteUsageLog("usage.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Premium
----HEADER END----
----LOG BEGIN----
00:00:02|AddinLoading|DummyRobotStudioAddin
----LOG END----
""");

    logs.WriteErrorLog("error.rslog", """
2026-04-04 21:48:08
RobotStudio .NET exception: Unable to load one or more of the requested types. Retrieve the LoaderExceptions property for more information.
============================================
Base exception: ReflectionTypeLoadException
Unable to load one or more of the requested types. Retrieve the LoaderExceptions property for more information.
   at System.Reflection.RuntimeModule.GetTypes(RuntimeModule module)
   at System.Reflection.RuntimeModule.GetTypes()
   at System.Reflection.Assembly.GetTypes()
   at RobotStudio.ExtensionManagement.Addin.TryLoad(String[] args)
LoaderException: Could not load file or assembly 'RobotStudioMCP.Core.Station, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null' or one of its dependencies. The system cannot find the file specified.
""");

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles("DummyRobotStudioAddin", logs.UsageRoot, logs.ErrorRoot, maxFilesPerKind: 2);

    Assert.IsTrue(result.LoadFailureEntries.Any(entry => entry.Contains("ReflectionTypeLoadException", StringComparison.Ordinal)));
    Assert.IsTrue(result.LoadFailureEntries.Any(entry => entry.Contains("RobotStudioMCP.Core.Station", StringComparison.Ordinal)));
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestSessionLogs_PreservesFirstFailureEvidenceFromEntryPointExceptionChain()
  {
    using var logs = new LogDirectoriesScope("FirstFailureChain");

    logs.WriteUsageLog("RobotStudio-20260406-190054.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Premium
----HEADER END----
----LOG BEGIN----
00:00:01|AddinLoading|RobotStudioRosBridge
00:00:02|AddinLoaded|RobotStudioRosBridge
----LOG END----
""");

    var errorPath = logs.WriteErrorLog("RobotStudio-20260406-190054.rslog", """
2026-04-06 19:00:54
00:00:01|UserLogMessage|Error||An exception was thrown when invoking the AddIn entrypoint.
RobotStudio .NET exception: Exception has been thrown by the target of an invocation.
============================================
Base exception: TargetInvocationException
Exception has been thrown by the target of an invocation.
   at System.Reflection.RuntimeMethodInfo.Invoke(Object obj, BindingFlags invokeAttr, Binder binder, Object[] parameters, CultureInfo culture)
   at RobotStudio.ExtensionManagement.Addin.TryLoad(String[] args)
Inner exception: InvalidOperationException
Missing or invalid ROBOTSTUDIO_DEMO_MODE. Expected 'Groove welding' or 'Sheet-metal seam detection'.
   at RobotStudioRosBridge.AddIn.ReadRequiredDemoModeFromEnv() in C:\\Users\\niklas.skoglund\\source\\repos\\path-from-scan\\robotstudio\\src\\RobotStudioRosBridge.AddinMain\\AddInMain.cs:line 993
   at RobotStudioRosBridge.AddIn.AddinMain() in C:\\Users\\niklas.skoglund\\source\\repos\\path-from-scan\\robotstudio\\src\\RobotStudioRosBridge.AddinMain\\AddInMain.cs:line 46
""");

    // "Latest files" means the most recent RobotStudio session logs discovered under the usage/error roots.
    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles("RobotStudioRosBridge", logs.UsageRoot, logs.ErrorRoot, maxFilesPerKind: 2);

    Assert.IsTrue(result.LoadProgressEntries.Any(entry => entry.Contains("AddinLoaded|RobotStudioRosBridge", StringComparison.Ordinal)));
    StringAssert.Contains(result.FirstFailureLine, "An exception was thrown when invoking the AddIn entrypoint.");
    StringAssert.Contains(result.FirstFailureEvidence, errorPath);
    StringAssert.Contains(result.FirstFailureEvidence, "An exception was thrown when invoking the AddIn entrypoint.");
    Assert.IsTrue(result.IgnoredFailureEntries.Any(entry => entry.Contains("RobotStudio .NET exception", StringComparison.Ordinal)));
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_IgnoresUnrelatedFailureEntries()
  {
    using var logs = new LogDirectoriesScope("Unrelated");

    logs.WriteUsageLog("usage.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Premium
----HEADER END----
----LOG BEGIN----
00:00:02|AddinLoaded|RobotStudioMCP
----LOG END----
""");

    logs.WriteErrorLog("error.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Premium
----HEADER END----
----LOG BEGIN----
00:00:03|Error|Failed to load RobotStudioRosBridge.rsaddin
----LOG END----
""");

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles("RobotStudioMCP", logs.UsageRoot, logs.ErrorRoot, maxFilesPerKind: 2);

    Assert.AreEqual(0, result.LoadFailureEntries.Count);
    Assert.IsTrue(result.IgnoredFailureEntries.Any(entry => entry.Contains("RobotStudioRosBridge", StringComparison.Ordinal)));
    Assert.IsTrue(result.LoadProgressEntries.Any(entry => entry.Contains("AddinLoaded|RobotStudioMCP", StringComparison.Ordinal)));
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_NotBeforeUtcFiltersOlderLogs()
  {
    using var logs = new LogDirectoriesScope("Cutoff");

    var oldUsage = logs.WriteUsageLog("RobotStudio-20260330-220000.rslog", "00:00:01|AddinLoaded|RobotStudioMCP");
    var newUsage = logs.WriteUsageLog("RobotStudio-20260331-122901.rslog", "00:00:05|AddinLoaded|RobotStudioMCP");
    var oldError = logs.WriteErrorLog("RobotStudio-20260330-220500.rslog", "00:00:02|Error|Failed to load RobotStudioMCP.rsaddin");

    var cutoffUtc = DateTime.UtcNow.AddMinutes(-1);
    File.SetLastWriteTimeUtc(oldUsage, cutoffUtc.AddMinutes(-5));
    File.SetLastWriteTimeUtc(oldError, cutoffUtc.AddMinutes(-5));
    File.SetLastWriteTimeUtc(newUsage, cutoffUtc.AddSeconds(1));

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles(
        "RobotStudioMCP",
        logs.UsageRoot,
        logs.ErrorRoot,
        maxFilesPerKind: 5,
        notBeforeUtc: cutoffUtc);

    Assert.AreEqual(1, result.LoadProgressEntries.Count);
    Assert.IsTrue(result.LoadProgressEntries[0].Contains("RobotStudio-20260331-122901.rslog", StringComparison.Ordinal));
    Assert.AreEqual(0, result.LoadFailureEntries.Count);
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_FindsTargetFailureInUsageLogUserMessage()
  {
    using var logs = new LogDirectoriesScope("RequestedFailure");

    logs.WriteUsageLog("usage.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Premium
----HEADER END----
----LOG BEGIN----
00:00:03|UserLogMessage|Error||Failed to load RobotStudioRosBridge.rsaddin
----LOG END----
""");

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles(
        "RobotStudioRosBridge",
        logs.UsageRoot,
        logs.ErrorRoot,
        maxFilesPerKind: 2);

    Assert.IsTrue(result.LoadFailureEntries.Any(entry => entry.Contains("Failed to load RobotStudioRosBridge.rsaddin", StringComparison.Ordinal)));
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_NotBeforeUtc_IncludesByFileNameTimestampFallback()
  {
    using var logs = new LogDirectoriesScope("NameFallback");

    var cutoffUtc = new DateTime(2026, 3, 31, 7, 46, 0, DateTimeKind.Local).ToUniversalTime();
    var usagePath = logs.WriteUsageLog("RobotStudio-20260331-074705.rslog", "00:00:03|UserLogMessage|Error||Failed to load RobotStudioRosBridge.rsaddin");
    File.SetLastWriteTimeUtc(usagePath, cutoffUtc.AddMinutes(-10));

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles(
        "RobotStudioRosBridge",
        logs.UsageRoot,
        logs.ErrorRoot,
        maxFilesPerKind: 2,
        notBeforeUtc: cutoffUtc);

    Assert.IsTrue(result.LoadFailureEntries.Any(entry => entry.Contains("Failed to load RobotStudioRosBridge.rsaddin", StringComparison.Ordinal)));
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_NotBeforeUtc_ExcludesTrulyStaleLog()
  {
    using var logs = new LogDirectoriesScope("StaleCutoff");

    var cutoffUtc = new DateTime(2026, 3, 31, 7, 46, 0, DateTimeKind.Local).ToUniversalTime();
    var usagePath = logs.WriteUsageLog("RobotStudio-20260331-073000.rslog", "00:00:03|UserLogMessage|Error||Failed to load RobotStudioRosBridge.rsaddin");
    File.SetLastWriteTimeUtc(usagePath, cutoffUtc.AddMinutes(-10));

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles(
        "RobotStudioRosBridge",
        logs.UsageRoot,
        logs.ErrorRoot,
        maxFilesPerKind: 2,
        notBeforeUtc: cutoffUtc);

    Assert.AreEqual(0, result.LoadFailureEntries.Count);
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_IgnoresUIThreadWatchdogDeadlockAsBenign()
  {
    using var logs = new LogDirectoriesScope("UIThreadWatchdog");

    logs.WriteUsageLog("RobotStudio-20260406-190054.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Premium
----HEADER END----
----LOG BEGIN----
00:00:01|AddinLoading|RobotStudioMCP
----LOG END----
""");

    logs.WriteErrorLog("RobotStudio-20260406-190054.rslog", """
2026-04-06 19:00:54
00:00:42.2228706|UIThreadWatchdog|Error|Possible deadlock on UI thread
""");

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles(
        "RobotStudioMCP",
        logs.UsageRoot,
        logs.ErrorRoot,
        maxFilesPerKind: 2);

    Assert.IsNull(result.FirstFailureLine);
    Assert.IsNull(result.FirstFailureEvidence);
    Assert.AreEqual(0, result.LoadFailureEntries.Count);
    Assert.IsTrue(result.IgnoredFailureEntries.Any(entry => entry.Contains("UIThreadWatchdog|Error|Possible deadlock on UI thread", StringComparison.Ordinal)));

    var classification = RobotStudioHealthTools.ClassifyAddinLoad(result);
    Assert.AreNotEqual("ADDIN_LOAD_FAILED_WITH_EXCEPTION", classification.Code);
    Assert.AreEqual("loading", classification.Status);
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_DoesNotIgnoreTargetAddinFailureContainingBenignSubstring()
  {
    using var logs = new LogDirectoriesScope("UIThreadWatchdogTargetCollision");

    logs.WriteUsageLog("RobotStudio-20260406-190054.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Premium
----HEADER END----
----LOG BEGIN----
00:00:01|AddinLoading|RobotStudioMCP
----LOG END----
""");

    logs.WriteErrorLog("RobotStudio-20260406-190054.rslog", """
2026-04-06 19:00:54
00:00:42.2228706|Error|RobotStudioMCP UIThreadWatchdog integration threw an Exception during load.
""");

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles(
        "RobotStudioMCP",
        logs.UsageRoot,
        logs.ErrorRoot,
        maxFilesPerKind: 2);

    Assert.IsNotNull(result.FirstFailureLine);
    Assert.AreEqual(1, result.LoadFailureEntries.Count);
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_IgnoresSilentPlatformExceptionAsBenign()
  {
    // Reproduces a real RobotStudio 2026 boot found via live verification: RobotStudio
    // logs its own generic unhandled-exception telemetry as "...|Exception|<guid>|<Type>|Silent"
    // for exceptions it judged not worth showing the user. The example below
    // (RobotStudio.UI.Rapid.CodeSnippetManager failing to parse a RAPID code-snippet
    // directory) is recognizably unrelated to add-in loading: it's a different
    // internal namespace than add-in loading (RobotStudio.ExtensionManagement.Addin.TryLoad,
    // see CollectAddinLoadLogEvidenceFromLatestFiles_FlagsGenericLoaderExceptionsAsLoadFailure
    // below), it's parsing an XML resource rather than invoking an add-in entrypoint,
    // and no add-in name appears anywhere in the trace. That's inference from
    // namespace/semantic recognition, not a documented ABB guarantee - whether a
    // genuine add-in-load failure could ever be tagged |Silent without naming the
    // add-in is an open question, noted as a limitation in
    // docs/watchdog-addin-load-classification.md.
    //
    // That must not taint an add-in that actually loaded successfully with
    // HadStartupException - IsDefinitiveAddinLoadFailure treats HadStartupException
    // as a hard failure regardless of status, so this previously made `ready`
    // report false on an otherwise clean boot.
    using var logs = new LogDirectoriesScope("SilentPlatformException");

    logs.WriteUsageLog("RobotStudio-20261017-072822.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Trial
----HEADER END----
----LOG BEGIN----
00:00:08.3646240|Exception|3ff05a01-deb1-4ce9-8f6e-3be8cbcfcb03|System.Xml.XmlException|Silent
----EXCEPTION BEGIN----
Data at the root level is invalid. Line 1, position 1.
   at System.Xml.XmlTextReaderImpl.Throw(Exception e)
   at System.Xml.Linq.XDocument.Load(String uri, LoadOptions options)
   at RobotStudio.UI.Rapid.CodeSnippetManager.LoadSnippetsRec(CommandBarPopup popup, String dir)
----EXCEPTION END----
00:00:26.1307513|AddinLoading|RobotStudioMCP
00:00:26.1358771|AddinLoaded|RobotStudioMCP
----LOG END----
""");

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles(
        "RobotStudioMCP",
        logs.UsageRoot,
        logs.ErrorRoot,
        maxFilesPerKind: 2);

    Assert.IsNull(result.FirstFailureLine);
    Assert.IsNull(result.FirstFailureEvidence);
    Assert.IsTrue(result.IgnoredFailureEntries.Any(entry => entry.Contains("System.Xml.XmlException|Silent", StringComparison.Ordinal)));
    Assert.IsTrue(result.IgnoredFailureEntries.Any(entry => entry.Contains("CodeSnippetManager.LoadSnippetsRec", StringComparison.Ordinal)));

    var classification = RobotStudioHealthTools.ClassifyAddinLoad(result);
    Assert.AreEqual("ADDIN_LOAD_SUCCEEDED", classification.Code);
    Assert.AreEqual("loaded", classification.Status);
    Assert.IsTrue(classification.Ok);
    Assert.IsFalse(classification.HadStartupException);
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_DoesNotIgnoreSilentExceptionMentioningTargetAddin()
  {
    using var logs = new LogDirectoriesScope("SilentExceptionTargetCollision");

    logs.WriteUsageLog("RobotStudio-20261017-072822.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Trial
----HEADER END----
----LOG BEGIN----
00:00:08|Exception|3ff05a01-deb1-4ce9-8f6e-3be8cbcfcb03|RobotStudioMCP.CustomException|Silent
00:00:26|AddinLoading|RobotStudioMCP
----LOG END----
""");

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles(
        "RobotStudioMCP",
        logs.UsageRoot,
        logs.ErrorRoot,
        maxFilesPerKind: 2);

    Assert.IsNotNull(result.FirstFailureLine);
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_IgnoresWholeSilentExceptionDumpBlock()
  {
    // The "----EXCEPTION BEGIN----"/stack-frame/"----EXCEPTION END----" lines that
    // follow a benign Silent exception's summary line independently match
    // LooksLikeAddinFailure (they contain the word "Exception" too, e.g.
    // "Throw(Exception e)") - without block tracking, the *second* line of this
    // same dump would re-trigger as a brand new, unfiltered firstFailureLine right
    // after the real one was correctly suppressed.
    using var logs = new LogDirectoriesScope("SilentExceptionDumpBlock");

    logs.WriteUsageLog("RobotStudio-20261017-073628.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Trial
----HEADER END----
----LOG BEGIN----
00:00:03|Exception|8a3c337a-25ab-45ef-a8a1-2ac71a01adaf|System.Xml.XmlException|Silent
----EXCEPTION BEGIN----
   at System.Xml.XmlTextReaderImpl.Throw(Exception e)
   at RobotStudio.UI.Rapid.CodeSnippetManager.LoadSnippetsRec(CommandBarPopup popup, String dir)
----EXCEPTION END----
00:00:16|AddinLoading|RobotStudioMCP
00:00:16|AddinLoaded|RobotStudioMCP
----LOG END----
""");

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles(
        "RobotStudioMCP",
        logs.UsageRoot,
        logs.ErrorRoot,
        maxFilesPerKind: 2);

    Assert.IsNull(result.FirstFailureLine);
    Assert.IsNull(result.FirstFailureEvidence);
    Assert.IsTrue(result.IgnoredFailureEntries.Any(e => e.Contains("----EXCEPTION BEGIN----", StringComparison.Ordinal)));
    Assert.IsTrue(result.IgnoredFailureEntries.Any(e => e.Contains("XmlTextReaderImpl.Throw", StringComparison.Ordinal)));

    var classification = RobotStudioHealthTools.ClassifyAddinLoad(result);
    Assert.AreEqual("ADDIN_LOAD_SUCCEEDED", classification.Code);
    Assert.IsFalse(classification.HadStartupException);
  }

  [TestMethod]
  public async Task VerifyAddinMcpReady_WithUnrelatedSilentExceptionBeforeLoad_IsNotBlockedByStartupException()
  {
    using var logs = new LogDirectoriesScope("SilentExceptionBeforeLoad");

    logs.WriteUsageLog("RobotStudio-20261017-072822.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Trial
----HEADER END----
----LOG BEGIN----
00:00:01.9033015|AddinAdded|RobotStudioMCP|General
00:00:08.3646240|Exception|3ff05a01-deb1-4ce9-8f6e-3be8cbcfcb03|System.Xml.XmlException|Silent
----EXCEPTION BEGIN----
Data at the root level is invalid. Line 1, position 1.
   at System.Xml.XmlTextReaderImpl.Throw(Exception e)
   at System.Xml.Linq.XDocument.Load(String uri, LoadOptions options)
   at RobotStudio.UI.Rapid.CodeSnippetManager.LoadSnippetsRec(CommandBarPopup popup, String dir)
----EXCEPTION END----
00:00:26.1307513|AddinLoading|RobotStudioMCP
00:00:26.1358771|AddinLoaded|RobotStudioMCP
----LOG END----
""");

    var sut = CreateSut();

    var result = await sut.VerifyAddinMcpReady(
        "http://127.0.0.1:1",
        timeoutSeconds: 1,
        retryDelayMs: 100,
        usageLogsRoot: logs.UsageRoot,
        errorLogsRoot: logs.ErrorRoot);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.AreEqual("loaded", (string)json["runtimeReadiness"]!["mcpAddinLoad"]!["status"]!);
    Assert.IsFalse((bool)json["runtimeReadiness"]!["mcpAddinLoad"]!["hadStartupException"]!);
    Assert.AreNotEqual("addin_load", (string)json["stageDiagnostics"]!["stage"]!);
  }

  [TestMethod]
  public async Task VerifyAddinMcpReady_WithNoStartupExceptionAtAll_AlsoReportsCleanLoad()
  {
    // Same AddinAdded/AddinLoading/AddinLoaded sequence as
    // VerifyAddinMcpReady_WithUnrelatedSilentExceptionBeforeLoad_IsNotBlockedByStartupException,
    // minus the benign exception - the outcome must be identical, proving the
    // exception's presence in that other test isn't what makes it pass.
    using var logs = new LogDirectoriesScope("NoStartupException");

    logs.WriteUsageLog("RobotStudio-20261017-072822.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Trial
----HEADER END----
----LOG BEGIN----
00:00:01.9033015|AddinAdded|RobotStudioMCP|General
00:00:26.1307513|AddinLoading|RobotStudioMCP
00:00:26.1358771|AddinLoaded|RobotStudioMCP
----LOG END----
""");

    var sut = CreateSut();

    var result = await sut.VerifyAddinMcpReady(
        "http://127.0.0.1:1",
        timeoutSeconds: 1,
        retryDelayMs: 100,
        usageLogsRoot: logs.UsageRoot,
        errorLogsRoot: logs.ErrorRoot);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.AreEqual("loaded", (string)json["runtimeReadiness"]!["mcpAddinLoad"]!["status"]!);
    Assert.IsFalse((bool)json["runtimeReadiness"]!["mcpAddinLoad"]!["hadStartupException"]!);
    Assert.AreNotEqual("addin_load", (string)json["stageDiagnostics"]!["stage"]!);
  }

  [TestMethod]
  public void ClassifyAddinLoad_WhenAddinAddedIsPowerPac_ReportsAwaitingManualActivation()
  {
    var evidence = new RobotStudioAddinLoadLogEvidence
    {
      PackageId = "SamplePowerPac",
      LoadProgressEntries = new[] { "C:\\Logs\\usage.rslog: 00:00:02.9267332|AddinAdded|SamplePowerPac|PowerPac" }
    };

    var result = RobotStudioHealthTools.ClassifyAddinLoad(evidence);

    Assert.AreEqual("ADDIN_AWAITING_ACTIVATION", result.Code);
    Assert.AreEqual("awaiting manual activation", result.Status);
    Assert.IsTrue(result.Ok);
    StringAssert.Contains(result.Hint, "awaiting manual activation");
  }

  [TestMethod]
  public void ClassifyAddinLoad_WhenAddinAddedTypeFieldIsNotPowerPac_DoesNotReportAwaitingActivation()
  {
    var evidence = new RobotStudioAddinLoadLogEvidence
    {
      PackageId = "LegacyPowerPacMigrationTool",
      LoadProgressEntries = new[] { "C:\\Logs\\PowerPac\\usage.rslog: 00:00:02.9267332|AddinAdded|LegacyPowerPacMigrationTool|General" }
    };

    var result = RobotStudioHealthTools.ClassifyAddinLoad(evidence);

    Assert.AreEqual("ADDIN_LOAD_UNKNOWN", result.Code);
    Assert.AreEqual("added", result.Status);
    Assert.IsFalse(result.Ok);
  }

  [TestMethod]
  public void ClassifyAddinLoad_WhenAddedAsPowerPacWithUnrelatedException_StillReportsAwaitingActivation()
  {
    // Reproduces a real RobotStudio 2026 boot: an unrelated internal exception
    // (RobotStudio.UI.Rapid.CodeSnippetManager failing to parse a snippet directory)
    // fired before a PowerPac add-in's own AddinAdded entry. Per
    // docs/watchdog-addin-load-classification.md, a failure that doesn't
    // mention this id must not downgrade a PowerPac add-in that's otherwise
    // correctly awaiting manual activation.
    var evidence = new RobotStudioAddinLoadLogEvidence
    {
      PackageId = "SamplePowerPac",
      LoadProgressEntries = new[] { "C:\\Logs\\usage.rslog: 00:00:02|AddinAdded|SamplePowerPac|PowerPac" },
      LoadFailureEntries = Array.Empty<string>(),
      FirstFailureLine = "00:00:01|Exception|...|System.Xml.XmlException|Silent",
      FirstFailureEvidence = "C:\\Logs\\usage.rslog: 00:00:01|Exception|...|System.Xml.XmlException|Silent"
    };

    var result = RobotStudioHealthTools.ClassifyAddinLoad(evidence);

    Assert.AreEqual("ADDIN_AWAITING_ACTIVATION", result.Code);
    Assert.AreEqual("awaiting manual activation", result.Status);
    Assert.IsTrue(result.Ok);
  }

  [TestMethod]
  public void ClassifyAddinLoad_WhenLoadingWithUnrelatedException_StillReportsLoading()
  {
    var evidence = new RobotStudioAddinLoadLogEvidence
    {
      PackageId = "RobotStudioMCP",
      LoadProgressEntries = new[] { "C:\\Logs\\usage.rslog: 00:00:02|AddinLoading|RobotStudioMCP" },
      LoadFailureEntries = Array.Empty<string>(),
      FirstFailureLine = "00:00:01|Exception|...|System.Xml.XmlException|Silent",
      FirstFailureEvidence = "C:\\Logs\\usage.rslog: 00:00:01|Exception|...|System.Xml.XmlException|Silent"
    };

    var result = RobotStudioHealthTools.ClassifyAddinLoad(evidence);

    Assert.AreEqual("ADDIN_LOAD_UNKNOWN", result.Code);
    Assert.AreEqual("loading", result.Status);
    Assert.IsFalse(result.Ok);
  }

  [TestMethod]
  public void ClassifyAddinLoad_WhenNoProgressSignalAtAllWithUnexplainedException_StillFallsBackToFailed()
  {
    // The one documented exception to the scoping rule: with zero evidence this id
    // was ever recognized, the first unexplained exception is still attributed to
    // it as a last resort (some entrypoint crashes never log the package id).
    var evidence = new RobotStudioAddinLoadLogEvidence
    {
      PackageId = "SomeAddin",
      LoadProgressEntries = Array.Empty<string>(),
      LoadFailureEntries = Array.Empty<string>(),
      FirstFailureLine = "00:00:01|Exception|...|System.ArgumentOutOfRangeException|UserVisible",
      FirstFailureEvidence = "C:\\Logs\\usage.rslog: 00:00:01|Exception|...|System.ArgumentOutOfRangeException|UserVisible"
    };

    var result = RobotStudioHealthTools.ClassifyAddinLoad(evidence);

    Assert.AreEqual("ADDIN_LOAD_FAILED_WITH_EXCEPTION", result.Code);
    Assert.AreEqual("failed", result.Status);
    Assert.IsFalse(result.Ok);
  }

  [TestMethod]
  public void CollectAddinLoadLogEvidenceFromLatestFiles_TracksActivatedPPForPowerPac()
  {
    using var logs = new LogDirectoriesScope("PowerPac");

    logs.WriteUsageLog("RobotStudio-20261014-164051.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
LicenseLevel=Trial
----HEADER END----
----LOG BEGIN----
00:00:02.9267332|AddinAdded|SamplePowerPac|PowerPac
00:00:21.4777463|AddinLoading|SamplePowerPac
00:00:21.7223656|AddinLoaded|SamplePowerPac
00:00:21.7975524|ActivatedPP|SamplePowerPac
----LOG END----
""");

    var result = RobotStudioHealthTools.CollectAddinLoadLogEvidenceFromLatestFiles(
        "SamplePowerPac",
        logs.UsageRoot,
        logs.ErrorRoot,
        maxFilesPerKind: 2);

    Assert.IsTrue(result.LoadProgressEntries.Any(entry => entry.Contains("ActivatedPP|SamplePowerPac", StringComparison.Ordinal)));

    var classification = RobotStudioHealthTools.ClassifyAddinLoad(result);
    Assert.AreEqual("ADDIN_LOAD_SUCCEEDED", classification.Code);
    Assert.AreEqual("activated", classification.Status);
    Assert.IsTrue(classification.Ok);
    Assert.IsTrue(classification.Evidence.Any(e => e.Contains("ActivatedPP|SamplePowerPac", StringComparison.Ordinal)));
  }

  [TestMethod]
  public void ParseLatestUsageLogLicenseInfo_ReadsRobotStudio2026TrialHeader()
  {
    using var temp = new TempDirectoryScope("Usage2026Trial");
    temp.CreateFile("RobotStudio-20261014-164051.rslog", """
RobotStudioUsage|V1
----HEADER BEGIN----
Licenses=
LicenseFeatures=
LicenseLevel=Trial
PremiumLicenseType=Standalone
----HEADER END----
----LOG BEGIN----
00:00:01|UIThreadWatchdog|Started
----LOG END----
""");

    var result = RobotStudioHealthTools.ParseLatestUsageLogLicenseInfo(temp.DirectoryPath);

    Assert.IsTrue(result.Found);
    Assert.AreEqual("Trial", result.LicenseLevel);
    Assert.AreEqual("Standalone", result.PremiumLicenseType);

    var readiness = RobotStudioHealthTools.EvaluateLicenseReadinessForAddinLoad(result);
    Assert.IsTrue(readiness.ok);
    StringAssert.Contains(readiness.reason, "non-Free");
  }

  [TestMethod]
  public void ParseRsaddinManifest_ReadsAddInTypeAndLicenseFeature()
  {
    using var temp = new TempDirectoryScope("RsaddinManifest");
    var rsaddinPath = temp.CreateFile("ArcWelding.rsaddin", """
<?xml version="1.0" encoding="utf-8" ?>
<RobotStudioAddIn xmlns="urn:abb-robotics-robotstudio-addin">
  <ApplicationId>com.abb.in.arcwelding</ApplicationId>
  <AddInType>PowerPac</AddInType>
  <LicenseFeature>ArcWeld_PowerPac</LicenseFeature>
  <Assembly>
    <FileName>ArcWelding.dll</FileName>
  </Assembly>
</RobotStudioAddIn>
""");

    var (addinType, licenseFeature) = RobotStudioHealthTools.ParseRsaddinManifest(rsaddinPath);

    Assert.AreEqual("PowerPac", addinType);
    Assert.AreEqual("ArcWeld_PowerPac", licenseFeature);
  }

  [TestMethod]
  public void GetAddinLoadState_WhenRsaddinRequiresMissingLicenseFeature_ReportsLicenseFeatureMissing()
  {
    using var temp = new TempDirectoryScope("RsaddinLicMissing");
    var usageDir = temp.CreateDirectory("usage");
    var errorDir = temp.CreateDirectory("error");

    var rsaddinPath = temp.CreateFile("ArcWelding.rsaddin", """
<?xml version="1.0" encoding="utf-8" ?>
<RobotStudioAddIn xmlns="urn:abb-robotics-robotstudio-addin">
  <ApplicationId>com.abb.in.arcwelding</ApplicationId>
  <AddInType>PowerPac</AddInType>
  <LicenseFeature>ArcWeld_PowerPac</LicenseFeature>
</RobotStudioAddIn>
""");

    temp.CreateFile("usage/usage.rslog", "00:00:01|AddinAdded|ArcWelding|General");

    var license = new RobotStudioLicenseDiagnostics
    {
      Found = true,
      LicenseLevel = "Premium",
      LicenseFeatures = "Premium|OtherFeature"
    };

    var state = RobotStudioHealthTools.GetAddinLoadState(
        "RobotStudioMCP",
        rsaddinPath,
        usageDir,
        errorDir,
        notBeforeUtc: null,
        licenseDiagnostics: license);

    Assert.AreEqual("ADDIN_LOAD_LICENSE_FEATURE_MISSING", state.RsaddinLoad.Code);
    Assert.AreEqual("license_feature_missing", state.RsaddinLoad.Status);
    Assert.IsFalse(state.RsaddinLoad.Ok);
    StringAssert.Contains(state.RsaddinLoad.Hint, "ArcWeld_PowerPac");
  }

  [TestMethod]
  public void GetAddinLoadState_WhenLicenseIsTrial_DoesNotReportLicenseFeatureMissing()
  {
    using var temp = new TempDirectoryScope("RsaddinLicTrial");
    var usageDir = temp.CreateDirectory("usage");
    var errorDir = temp.CreateDirectory("error");

    var rsaddinPath = temp.CreateFile("ArcWelding.rsaddin", """
<?xml version="1.0" encoding="utf-8" ?>
<RobotStudioAddIn xmlns="urn:abb-robotics-robotstudio-addin">
  <ApplicationId>com.abb.in.arcwelding</ApplicationId>
  <AddInType>PowerPac</AddInType>
  <LicenseFeature>ArcWeld_PowerPac</LicenseFeature>
</RobotStudioAddIn>
""");

    temp.CreateFile("usage/usage.rslog", "00:00:01|AddinAdded|ArcWelding|General");

    var license = new RobotStudioLicenseDiagnostics
    {
      Found = true,
      LicenseLevel = "Trial",
      LicenseFeatures = ""
    };

    var state = RobotStudioHealthTools.GetAddinLoadState(
        "RobotStudioMCP",
        rsaddinPath,
        usageDir,
        errorDir,
        notBeforeUtc: null,
        licenseDiagnostics: license);

    Assert.AreNotEqual("ADDIN_LOAD_LICENSE_FEATURE_MISSING", state.RsaddinLoad.Code);
    Assert.AreEqual("ADDIN_LOAD_UNKNOWN", state.RsaddinLoad.Code);
    Assert.AreEqual("added", state.RsaddinLoad.Status);
  }

  [TestMethod]
  public void ReadRobotStudioLogTail_ReturnsTailContent()
  {
    using var temp = new TempDirectoryScope("LogTail");
    var logPath = temp.CreateFile("tail.rslog", "line-1\nline-2\nline-3\nline-4\n");

    var result = RobotStudioHealthTools.ReadRobotStudioLogTail(logPath, tailLines: 2, maxChars: 2000, usageLogsRoot: temp.DirectoryPath);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsTrue((bool)json["ok"]!);
    StringAssert.Contains((string)json["content"]!, "line-3");
    StringAssert.Contains((string)json["content"]!, "line-4");
  }

  [TestMethod]
  public void ReadRobotStudioLogTail_OutsideKnownRoots_RejectedWithoutReadingFile()
  {
    using var temp = new TempDirectoryScope("LogTailOutsideRoots");
    var logPath = temp.CreateFile("tail.rslog", "line-1\n");

    // No usageLogsRoot/errorLogsRoot override, so the file (outside the ProgramData defaults) must be rejected.
    var result = RobotStudioHealthTools.ReadRobotStudioLogTail(logPath, tailLines: 2, maxChars: 2000);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["ok"]!);
    StringAssert.Contains((string)json["error"]!, "UsageLogs");
  }

  [TestMethod]
  public void ReadRobotStudioLogTail_NonRslogExtension_Rejected()
  {
    using var temp = new TempDirectoryScope("LogTailWrongExtension");
    var logPath = temp.CreateFile("tail.txt", "line-1\n");

    var result = RobotStudioHealthTools.ReadRobotStudioLogTail(logPath, tailLines: 2, maxChars: 2000, usageLogsRoot: temp.DirectoryPath);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["ok"]!);
    StringAssert.Contains((string)json["error"]!, "rslog");
  }
}

[TestClass]
public class RobotStudioPackageToolsTests
{
  [TestMethod]
  public void InstallRspakPackage_WhenValidArchive_InstallsIntoRoot()
  {
    using var tempRoot = new TempDirectoryScope("RspakInstall");
    var rspakPath = tempRoot.PathTo("RobotStudioMCP-1.2.3.rspak");
    using (var archive = ZipFile.Open(rspakPath, ZipArchiveMode.Create))
    {
      archive.CreateEntry("RobotStudioMCP-1.2.3/manifest.xml");
      archive.CreateEntry("RobotStudioMCP-1.2.3/RobotStudio/Add-In/RobotStudioMCP.dll");
      archive.CreateEntry("RobotStudioMCP-1.2.3/RobotStudio/Add-In/RobotStudioMCP.rsaddin");
    }

    var result = RobotStudioPackageTools.InstallRspakPackage(rspakPath, distributionPackagesRoot: tempRoot.DirectoryPath, overwriteExisting: true, dryRun: false);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsTrue((bool)json["installed"]!);
    var packageDir = tempRoot.PathTo("RobotStudioMCP-1.2.3");
    Assert.IsTrue(Directory.Exists(packageDir));
    Assert.IsTrue(File.Exists(Path.Combine(packageDir, "manifest.xml")));
  }

  [TestMethod]
  public void ResolveDistributionPackageRoots_WithoutOverrides_UsesUserLocalRoots()
  {
    var roots = RobotStudioPackageTools.ResolveDistributionPackageRoots(null, null).ToArray();

    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var expectedPrimary = Path.Combine(localAppData, "ABB", "DistributionPackages");
    var expectedSecondary = Path.Combine(localAppData, "ABB", "DistributionPackages2");

    CollectionAssert.Contains(roots, expectedPrimary);
    CollectionAssert.Contains(roots, expectedSecondary);
    Assert.IsFalse(roots.Any(root => root.StartsWith(@"C:\ProgramData\ABB\DistributionPackages", StringComparison.OrdinalIgnoreCase)));
  }

  [TestMethod]
  public void RspakList_WhenValidPackageExists_ReturnsManifestPath()
  {
    using var tempRoot = new TempDirectoryScope("RspakList");
    var manifestPath = tempRoot.CreateFile("RobotStudioMCP-1.0.0/manifest.xml", "<DistributionPackage />");

    var result = RobotStudioPackageTools.RspakList(packageId: "RobotStudioMCP", distributionPackagesRoot: tempRoot.DirectoryPath);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsTrue((bool)json["listed"]!);
    Assert.AreEqual(1, (int)json["count"]!);
    Assert.AreEqual("RobotStudioMCP-1.0.0", (string)json["packages"]![0]!["packageDirectoryName"]!);
    Assert.AreEqual(manifestPath, (string)json["packages"]![0]!["manifestPath"]!);
  }

  [TestMethod]
  public void RspakList_WhenManifestMissing_ReturnsGeneralError()
  {
    using var tempRoot = new TempDirectoryScope("RspakListMissingManifest");
    tempRoot.CreateDirectory("RobotStudioMCP-1.0.0");

    var result = RobotStudioPackageTools.RspakList(packageId: "RobotStudioMCP", distributionPackagesRoot: tempRoot.DirectoryPath);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["listed"]!);
    StringAssert.Contains((string)json["error"]!, "manifest.xml");
  }

  [TestMethod]
  public void RspakUninstall_WhenMultipleMatchesAndNotForced_ReturnsAmbiguous()
  {
    using var tempRoot = new TempDirectoryScope("RspakUninstall");
    tempRoot.CreateDirectory("RobotStudioMCP-0.1.0");
    tempRoot.CreateDirectory("RobotStudioMCP-0.2.0");

    var result = RobotStudioPackageTools.RspakUninstall(packageId: "RobotStudioMCP", distributionPackagesRoot: tempRoot.DirectoryPath, removeAllMatches: false, dryRun: false);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["uninstalled"]!);
    Assert.IsTrue((bool)json["ambiguous"]!);
  }

  [TestMethod]
  public void RspakUninstall_WhenExactDirectoryProvided_RemovesTarget()
  {
    using var tempRoot = new TempDirectoryScope("RspakUninstallExact");
    var target = tempRoot.CreateDirectory("RobotStudioMCP-9.9.9");

    var result = RobotStudioPackageTools.RspakUninstall(packageDirectoryName: "RobotStudioMCP-9.9.9", distributionPackagesRoot: tempRoot.DirectoryPath, dryRun: false);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsTrue((bool)json["uninstalled"]!);
    Assert.IsFalse(Directory.Exists(target));
  }
}

[TestClass]
public class RobotStudioLifecycleToolsTests
{
  private static readonly string[] ExpectedRobotStudioProcessNames = ["RobotStudio"];
  private static readonly string[] ExpectedRobVcProcessNames = ["RobVC"];

  private static (RobotStudioLifecycleTools Sut, CapturingProcessLauncher Launcher) CreateLifecycleSut(
      Process? liveProcess = null,
      CapturingProcessKiller? killer = null)
  {
    var current = liveProcess ?? Process.GetCurrentProcess();
    var launcher = new CapturingProcessLauncher(current);
    var store = new FakeStateStore(new ProcessTrackingState());
    var sut = killer is null
        ? new RobotStudioLifecycleTools(store, launcher)
        : new RobotStudioLifecycleTools(store, launcher, killer);
    return (sut, launcher);
  }

  [TestMethod]
  public async Task StartRobotStudioProcess_WhenTrackedProcessExists_ReturnsAlreadyRunning()
  {
    using var current = Process.GetCurrentProcess();
    var store = new FakeStateStore(new ProcessTrackingState(), trackedLiveProcess: current);
    var sut = new RobotStudioLifecycleTools(store);

    var result = await sut.StartRobotStudioProcess(timeoutSeconds: 1);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["started"]!);
    Assert.IsTrue((bool)json["alreadyRunning"]!);
    Assert.AreEqual(current.Id, (int)json["processId"]!);
  }

  [TestMethod]
  public async Task StartRobotStudioProcess_WithRelativeRsaddinPath_ReturnsAbsolutePathError()
  {
    var store = new FakeStateStore(new ProcessTrackingState());
    var sut = new RobotStudioLifecycleTools(store);

    var requested = @"relative\CustomAddin.rsaddin";
    var result = await sut.StartRobotStudioProcess(rsaddinPath: requested, timeoutSeconds: 1);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["started"]!);
    Assert.AreEqual("The 'rsaddinPath' argument must be an absolute path.", (string)json["error"]!);
    Assert.AreEqual(requested, (string)json["rsaddinPath"]!);
  }

  [TestMethod]
  public async Task StartRobotStudioProcess_WithExplicitMissingRsaddinPath_ReportsProvidedPath()
  {
    var store = new FakeStateStore(new ProcessTrackingState());
    var sut = new RobotStudioLifecycleTools(store);

    var requested = @"C:\AnyVendor\AnyAddin\CustomAddin.rsaddin";
    var result = await sut.StartRobotStudioProcess(rsaddinPath: requested, timeoutSeconds: 1);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["started"]!);
    Assert.AreEqual("The .rsaddin file was not found.", (string)json["error"]!);
    Assert.AreEqual(requested, (string)json["rsaddinPath"]!);
  }

  [TestMethod]
  public async Task StartRobotStudioProcess_WithStationPath_PassesQuotedStationPathToLauncher()
  {
    using var temp = new TempDirectoryScope("Station");
    var tempStationPath = temp.CreateFile("fixture.rsstnx", "fixture");

    var (sut, launcher) = CreateLifecycleSut();

    var result = await sut.StartRobotStudioProcess(
        robotStudioExePath: @"C:\ABB\RobotStudio\RobotStudio.exe",
        stationPath: tempStationPath,
        timeoutSeconds: 1);

    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsTrue((bool)json["started"]!);
    Assert.AreEqual(tempStationPath, (string)json["stationPath"]!);
    Assert.IsNotNull(launcher.LastStartInfo);
    StringAssert.Contains(launcher.LastStartInfo!.Arguments, "/LoadAddins");
    StringAssert.Contains(launcher.LastStartInfo!.Arguments, $"\"{tempStationPath}\"");
  }

  [TestMethod]
  public async Task StartRobotStudioProcess_AlwaysPassesLoadAddinsSwitchToLauncher()
  {
    var (sut, launcher) = CreateLifecycleSut();

    var result = await sut.StartRobotStudioProcess(
        robotStudioExePath: @"C:\ABB\RobotStudio\RobotStudio.exe",
        timeoutSeconds: 1);

    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsTrue((bool)json["started"]!);
    Assert.IsNotNull(launcher.LastStartInfo);
    StringAssert.Contains(launcher.LastStartInfo!.Arguments, "/LoadAddins");
  }

  [TestMethod]
  public async Task StartRobotStudioProcess_WithEnvironment_PassesProcessScopedVariablesToLauncher()
  {
    var (sut, launcher) = CreateLifecycleSut();

    var result = await sut.StartRobotStudioProcess(
        robotStudioExePath: @"C:\ABB\RobotStudio\RobotStudio.exe",
        environment: new Dictionary<string, string>
        {
          ["ROBOTSTUDIO_DEMO_MODE"] = "Groove welding",
          ["CUSTOM_FLAG"] = "123"
        },
        timeoutSeconds: 1);

    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsTrue((bool)json["started"]!);
    Assert.IsNotNull(launcher.LastStartInfo);
    Assert.IsFalse(launcher.LastStartInfo!.UseShellExecute);
    Assert.AreEqual("Groove welding", launcher.LastStartInfo.Environment["ROBOTSTUDIO_DEMO_MODE"]);
    Assert.AreEqual("123", launcher.LastStartInfo.Environment["CUSTOM_FLAG"]);
  }

  [TestMethod]
  public async Task StartRobotStudioProcess_WithKillAllRobotStudio_KillsRobotStudioOnlyBeforeLaunch()
  {
    var killer = new CapturingProcessKiller();
    var (sut, _) = CreateLifecycleSut(killer: killer);

    var result = await sut.StartRobotStudioProcess(
        robotStudioExePath: @"C:\ABB\RobotStudio\RobotStudio.exe",
        killAllRobotStudio: true,
        killAllRobVC: false,
        timeoutSeconds: 1);

    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsTrue((bool)json["started"]!);
    CollectionAssert.AreEqual(ExpectedRobotStudioProcessNames, killer.KilledProcessNames);
  }

  [TestMethod]
  public async Task StartRobotStudioProcess_WithKillAllRobVC_KillsRobVCOnlyBeforeLaunch()
  {
    var killer = new CapturingProcessKiller();
    var (sut, _) = CreateLifecycleSut(killer: killer);

    var result = await sut.StartRobotStudioProcess(
        robotStudioExePath: @"C:\ABB\RobotStudio\RobotStudio.exe",
        killAllRobotStudio: false,
        killAllRobVC: true,
        timeoutSeconds: 1);

    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsTrue((bool)json["started"]!);
    CollectionAssert.AreEqual(ExpectedRobVcProcessNames, killer.KilledProcessNames);
  }

  [TestMethod]
  public async Task StopStartedRobotStudioProcess_WhenNothingTracked_ReturnsNothingToStop()
  {
    var store = new FakeStateStore(new ProcessTrackingState());
    var sut = new RobotStudioLifecycleTools(store);

    var result = await sut.StopStartedRobotStudioProcess(gracefulTimeoutSeconds: 1);
    var json = JsonSerializer.SerializeToNode(result)!;

    Assert.IsFalse((bool)json["stopped"]!);
    Assert.IsTrue((bool)json["nothingToStop"]!);
  }
}

internal sealed class FakeStateStore : IRobotStudioProcessStateStore
{
  private readonly ProcessTrackingState _current;
  private readonly Process? _trackedLiveProcess;

  public FakeStateStore(ProcessTrackingState current, Process? trackedLiveProcess = null)
  {
    _current = current;
    _trackedLiveProcess = trackedLiveProcess;
  }

  public ProcessTrackingState Current => _current;

  public bool TryGetTrackedLiveProcess(out Process process, out string reason)
  {
    if (_trackedLiveProcess is not null)
    {
      process = _trackedLiveProcess;
      reason = "Tracked process is running.";
      return true;
    }

    process = null!;
    reason = "No tracked RobotStudio process.";
    return false;
  }

  public void SaveTrackedProcess(int processId, DateTime processStartTimeUtc)
  {
  }

  public void ClearTrackedProcess(string reason)
  {
  }
}

internal sealed class CapturingProcessLauncher : IRobotStudioProcessLauncher
{
  private readonly Process _processToReturn;

  public CapturingProcessLauncher(Process processToReturn)
  {
    _processToReturn = processToReturn;
  }

  public ProcessStartInfo? LastStartInfo { get; private set; }

  public Process? Start(ProcessStartInfo startInfo)
  {
    LastStartInfo = startInfo;
    return _processToReturn;
  }
}

internal sealed class CapturingProcessKiller : IRobotStudioProcessKiller
{
  public List<string> KilledProcessNames { get; } = new();

  public object KillAllByName(string processName)
  {
    KilledProcessNames.Add(processName);
    return new { processName, requested = true, killed = Array.Empty<object>(), errors = Array.Empty<string>() };
  }
}

internal sealed class TempDirectoryScope : IDisposable
{
  public string DirectoryPath { get; }

  public TempDirectoryScope(string prefix = "Test")
  {
    DirectoryPath = Path.Combine(Path.GetTempPath(), $"RobotStudioMCP-Watchdog-{prefix}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(DirectoryPath);
  }

  public string PathTo(string relativePath) => Path.Combine(DirectoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

  public string CreateFile(string relativePath, string content)
  {
    var fullPath = PathTo(relativePath);
    var parent = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(parent))
    {
      Directory.CreateDirectory(parent);
    }
    File.WriteAllText(fullPath, content);
    return fullPath;
  }

  public string CreateDirectory(string relativePath)
  {
    var fullPath = PathTo(relativePath);
    Directory.CreateDirectory(fullPath);
    return fullPath;
  }

  public void Dispose()
  {
    try
    {
      if (Directory.Exists(DirectoryPath))
      {
        Directory.Delete(DirectoryPath, recursive: true);
      }
    }
    catch
    {
    }
  }

  public static implicit operator string(TempDirectoryScope scope) => scope.DirectoryPath;
}

internal sealed class LogDirectoriesScope : IDisposable
{
  public TempDirectoryScope Usage { get; }
  public TempDirectoryScope Error { get; }

  public string UsageRoot => Usage.DirectoryPath;
  public string ErrorRoot => Error.DirectoryPath;

  public LogDirectoriesScope(string prefix = "Logs")
  {
    Usage = new TempDirectoryScope($"Usage{prefix}");
    Error = new TempDirectoryScope($"Error{prefix}");
  }

  public string WriteUsageLog(string fileName, string content) => Usage.CreateFile(fileName, content);

  public string WriteErrorLog(string fileName, string content) => Error.CreateFile(fileName, content);

  public void Dispose()
  {
    Usage.Dispose();
    Error.Dispose();
  }
}
