using System.Collections.Generic;

namespace RobotStudioMCP.AddinServer
{
  /// <summary>
  /// Optional tool module loaded by the RobotStudioMCP add-in from <c>RobotStudioMCP.Tools.*.dll</c>
  /// files next to the add-in assembly. Implementations need a public parameterless constructor.
  /// </summary>
  public interface IAddinToolModule
  {
    string Name { get; }

    IEnumerable<AddinMcpTool> CreateTools();
  }
}
