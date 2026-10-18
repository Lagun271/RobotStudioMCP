using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RobotStudioMCP.Watchdog.Tests;

[TestClass]
public sealed class RobotStudioCommandLineTests
{
  [TestMethod]
  public async Task TryRunAsync_NoArguments_ReturnsFalse()
  {
    var stateStore = new RobotStudioProcessStateStore();
    var cli = new RobotStudioCommandLine(stateStore);

    var handled = await cli.TryRunAsync(Array.Empty<string>());

    Assert.IsFalse(handled);
  }

  [TestMethod]
  public async Task TryRunAsync_HelpCommand_WritesCommandList()
  {
    var stateStore = new RobotStudioProcessStateStore();
    var cli = new RobotStudioCommandLine(stateStore);
    var originalOut = Console.Out;
    var writer = new StringWriter();

    try
    {
      Console.SetOut(writer);

      var handled = await cli.TryRunAsync(["help"]);

      Assert.IsTrue(handled);
      var output = writer.ToString();
      StringAssert.Contains(output, "RobotStudioMCP.Watchdog command surface");
      StringAssert.Contains(output, "start");
      StringAssert.Contains(output, "stop");
      StringAssert.Contains(output, "mcp");
    }
    finally
    {
      Console.SetOut(originalOut);
    }
  }

  [TestMethod]
  public async Task TryRunAsync_StartHelp_WritesStartOptions()
  {
    // Per-command help (System.CommandLine) shows each command's own options; the top-level "help"
    // list above intentionally only lists command names/descriptions, not nested options.
    var stateStore = new RobotStudioProcessStateStore();
    var cli = new RobotStudioCommandLine(stateStore);
    var originalOut = Console.Out;
    var writer = new StringWriter();

    try
    {
      Console.SetOut(writer);

      var handled = await cli.TryRunAsync(["start", "--help"]);

      // Regression test for the other bug the protocol review flagged: `start --help` used to be
      // parsed as an ignored flag and would actually launch RobotStudio. System.CommandLine's --help
      // short-circuits before the command's own action (RobotStudioLifecycleTools.StartRobotStudioProcess)
      // ever runs, so a passing assertion here means the action's process-launch side effect did not occur.
      Assert.IsTrue(handled);
      Assert.AreEqual(0, Environment.ExitCode);
      var output = writer.ToString();
      StringAssert.Contains(output, "--station-path");
      StringAssert.Contains(output, "--wait-ready");
    }
    finally
    {
      Console.SetOut(originalOut);
    }
  }

  [TestMethod]
  public async Task TryRunAsync_MistypedCommand_DoesNotStartServerAndReturnsNonZero()
  {
    // Regression test for the bug the protocol review flagged: a mistyped command used to silently
    // start the stdio MCP server (IsCliInvocation returned false, so the caller fell through to
    // running the server) instead of reporting an error.
    var stateStore = new RobotStudioProcessStateStore();
    var cli = new RobotStudioCommandLine(stateStore);
    var originalOut = Console.Out;
    var writer = new StringWriter();

    try
    {
      Console.SetOut(writer);

      var handled = await cli.TryRunAsync(["strat"]);

      Assert.IsTrue(handled);
      Assert.AreNotEqual(0, Environment.ExitCode);
    }
    finally
    {
      Console.SetOut(originalOut);
    }
  }

  [TestMethod]
  public async Task TryRunAsync_StatusCommand_WritesJsonStatus()
  {
    var stateStore = new RobotStudioProcessStateStore();
    var cli = new RobotStudioCommandLine(stateStore);
    var originalOut = Console.Out;
    var writer = new StringWriter();

    try
    {
      Console.SetOut(writer);

      var handled = await cli.TryRunAsync(["status"]);

      Assert.IsTrue(handled);
      var output = writer.ToString();
      StringAssert.Contains(output, "\"command\": \"status\"");
      StringAssert.Contains(output, "\"ok\": true");
    }
    finally
    {
      Console.SetOut(originalOut);
    }
  }
}
