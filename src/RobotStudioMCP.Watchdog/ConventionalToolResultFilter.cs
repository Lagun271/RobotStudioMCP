using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace RobotStudioMCP.Watchdog;

/// <summary>
/// Every tool method in this project reports business failure through a conventional boolean flag in
/// its returned object (<c>ok</c>/<c>installed</c>/<c>started</c>/... = <see langword="false"/>) rather
/// than throwing, because that same shape is also what reads it directly -- unit tests, the CLI, and
/// (critically) every tool method's declared return type, which is the bare <c>object</c>/<c>Task&lt;object&gt;</c>
/// every one of these methods already used before the protocol review. That convention never reached MCP
/// clients: the SDK only sets <see cref="CallToolResult.IsError"/> when a tool throws, so a client talking
/// over MCP saw every one of these as a successful call. This filter, registered once in Program.cs, is the
/// single place that turns the convention into that signal for every tool, instead of rewriting each method.
/// </summary>
/// <remarks>
/// Reads <see cref="CallToolResult.Content"/>'s text block rather than <see cref="CallToolResult.StructuredContent"/>.
/// <c>UseStructuredContent</c> on a tool whose declared return type is the bare <c>object</c> (every tool
/// here) makes the SDK wrap the actual payload one level deeper, under a synthetic top-level <c>result</c>
/// property, because it can't generate a real schema from <c>object</c> -- e.g. <c>{"ok":false}</c> becomes
/// <c>{"result":{"ok":false}}</c> on the wire. That's invisible to the official C# client (it unwraps on the
/// way in) but breaks any client that doesn't know that convention, including the hand-rolled Python MCP
/// client this repo's smoke tests use. The text content block has no such wrapping, so this filter (and
/// <c>UseStructuredContent</c> itself) was changed to use that instead, keeping the wire-visible JSON shape
/// identical to what it was before this project started setting <c>UseStructuredContent</c>.
/// </remarks>
internal static class ConventionalToolResultFilter
{
  private static readonly string[] SuccessFlagNames =
  {
    "ok", "installed", "uninstalled", "listed", "started", "stopped", "ready", "reachable"
  };

  public static CallToolResult ApplyConventionalIsError(CallToolResult result)
  {
    if (result.IsError == true)
    {
      return result;
    }

    var text = (result.Content?.Count ?? 0) > 0 ? (result.Content[0] as TextContentBlock)?.Text : null;
    if (string.IsNullOrWhiteSpace(text))
    {
      return result;
    }

    JsonElement content;
    try
    {
      content = JsonDocument.Parse(text).RootElement;
    }
    catch (JsonException)
    {
      return result;
    }

    if (content.ValueKind != JsonValueKind.Object)
    {
      return result;
    }

    foreach (var flagName in SuccessFlagNames)
    {
      if (content.TryGetProperty(flagName, out var flagValue) && flagValue.ValueKind == JsonValueKind.False)
      {
        result.IsError = true;
        break;
      }
    }

    return result;
  }
}
