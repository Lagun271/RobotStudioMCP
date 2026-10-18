using System;
using System.Collections.Generic;

namespace RobotStudioMCP.AddinServer
{
  public sealed class AddinMcpTool
  {
    public string Name { get; set; }
    public string Description { get; set; }
    public IDictionary<string, object> InputSchema { get; set; }
    public Func<IDictionary<string, object>, AddinMcpToolResult> Handler { get; set; }
  }

  public sealed class AddinMcpToolResult
  {
    public object StructuredContent { get; set; }
    public string Text { get; set; }
    public bool IsError { get; set; }

    public static AddinMcpToolResult Success(object structuredContent, string text = null)
        => new AddinMcpToolResult
        {
          StructuredContent = structuredContent,
          Text = text,
          IsError = false
        };

    public static AddinMcpToolResult Error(object structuredContent, string text = null)
        => new AddinMcpToolResult
        {
          StructuredContent = structuredContent,
          Text = text,
          IsError = true
        };
  }
}
