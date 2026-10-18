using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using RobotStudioMCP.Core.Station;

namespace RobotStudioMCP.Watchdog.Tests;

[TestClass]
public class StationStateTests
{
  [TestMethod]
  public void Create_WithReadyControllers_SetsReadyFlags()
  {
    var state = StationState.Create(
        hasActiveStation: true,
        controllers: new[]
        {
          new ControllerState { Name = "Controller-1", SystemState = "Ready" },
          new ControllerState { Name = "Controller-2", SystemState = "READY" }
        });

    Assert.IsTrue(state.HasActiveStation);
    Assert.AreEqual(2, state.ControllerCount);
    Assert.IsTrue(state.AllControllersReady);
    Assert.AreEqual("Controller-1", state.Controllers[0].Name);
  }

  [TestMethod]
  public void Create_WithStartedController_TreatsControllerAsReady()
  {
    var state = StationState.Create(
        hasActiveStation: true,
        controllers: new[]
        {
          new ControllerState { Name = "Controller-1", SystemState = "Started" }
        });

    Assert.IsTrue(state.HasActiveStation);
    Assert.AreEqual(1, state.ControllerCount);
    Assert.IsTrue(state.Controllers[0].IsReady);
    Assert.IsTrue(state.AllControllersReady);
  }

  [TestMethod]
  public async Task WaitForStationStateAsync_UsesDiAndMoq_ToPollUntilReady()
  {
    var mock = new Mock<IStationStateProvider>(MockBehavior.Strict);
    mock.SetupSequence(p => p.GetStationState())
        .Returns(StationState.Create(false, Array.Empty<ControllerState>()))
        .Returns(StationState.Create(true, new[] { new ControllerState { Name = "Controller-1", SystemState = "Stopped" } }))
        .Returns(StationState.Create(true, new[] { new ControllerState { Name = "Controller-1", SystemState = "Ready" } }));

    var services = new ServiceCollection();
    services.AddSingleton(mock.Object);
    services.AddSingleton<StationStatePollingConsumer>();

    using var serviceProvider = services.BuildServiceProvider();
    var consumer = serviceProvider.GetRequiredService<StationStatePollingConsumer>();

    var state = await consumer.WaitAsync(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));

    Assert.IsTrue(state.HasActiveStation);
    Assert.IsTrue(state.AllControllersReady);
    mock.Verify(p => p.GetStationState(), Times.Exactly(3));
  }

  [TestMethod]
  public async Task WaitForStationStateAsync_ReturnsLatestValueWhenTimeoutExpires()
  {
    var mock = new Mock<IStationStateProvider>(MockBehavior.Loose);
    mock.Setup(p => p.GetStationState())
        .Returns(StationState.Create(false, Array.Empty<ControllerState>()));

    var services = new ServiceCollection();
    services.AddSingleton(mock.Object);
    services.AddSingleton<StationStatePollingConsumer>();

    using var serviceProvider = services.BuildServiceProvider();
    var consumer = serviceProvider.GetRequiredService<StationStatePollingConsumer>();

    var state = await consumer.WaitAsync(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(1));

    Assert.IsFalse(state.HasActiveStation);
    Assert.IsFalse(state.AllControllersReady);
    mock.Verify(p => p.GetStationState(), Times.AtLeastOnce);
  }

  private sealed class StationStatePollingConsumer
  {
    private readonly IStationStateProvider _provider;

    public StationStatePollingConsumer(IStationStateProvider provider)
    {
      _provider = provider;
    }

    public Task<StationState> WaitAsync(TimeSpan timeout, TimeSpan pollInterval)
    {
      return _provider.WaitForStationStateAsync(timeout, pollInterval);
    }
  }
}
