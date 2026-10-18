using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RobotStudioMCP.AddinServer
{
  /// <summary>
  /// Hosts the add-in's tool surface over a Windows named pipe instead of an HTTP/MCP transport. This is
  /// an internal, same-build protocol between this add-in and <c>RobotStudioMCP.Watchdog</c> only -- it is
  /// never exposed as a real MCP endpoint, so it skips version negotiation, the initialize/initialized
  /// handshake, and notifications entirely (both ends always ship together). The watchdog is the only
  /// process that speaks real MCP to external clients; it forwards add-in tool calls here via
  /// <c>list_tools</c>/<c>call_tool</c> and relays the result back as a normal MCP tool result.
  ///
  /// Deliberately kept to exactly these two operations -- this class must not grow into a second,
  /// half-implemented MCP protocol stack. Each connection handles exactly one request/response pair.
  /// </summary>
  public sealed class AddinPipeServer : IDisposable
  {
    private readonly AddinMcpServerOptions _options;
    private readonly string _pipeName;
    private readonly Dictionary<string, AddinMcpTool> _tools = new Dictionary<string, AddinMcpTool>(StringComparer.Ordinal);
    private CancellationTokenSource _cts;
    private Task _acceptLoop;
    private DateTime _startedUtc;
    private volatile string _lastRequestError;

    public AddinPipeServer(
        AddinMcpServerOptions options,
        string pipeName,
        Func<IDictionary<string, object>> statusProvider = null,
        IEnumerable<AddinMcpTool> tools = null)
    {
      _options = options ?? new AddinMcpServerOptions();
      _pipeName = string.IsNullOrWhiteSpace(pipeName) ? throw new ArgumentException("Pipe name is required.", nameof(pipeName)) : pipeName;

      RegisterTool(new AddinMcpTool
      {
        Name = "get_robotstudio_status",
        Description = "Returns RobotStudio add-in process health and basic server status.",
        InputSchema = AddinMcpSchema.EmptySchema(),
        Handler = _ => AddinMcpToolResult.Success(GetStatusPayload(statusProvider))
      });

      if (tools != null)
      {
        foreach (var tool in tools)
        {
          RegisterTool(tool);
        }
      }
    }

    public string PipeName => _pipeName;

    // RobotStudio.exe and the watchdog's own process can end up in different logon sessions even
    // under the same Windows user account (observed in practice: connections from an otherwise
    // identical same-user client timed out against .NET's default pipe ACL). Granting the local
    // Users group explicit read/write access avoids that without opening the pipe up beyond this
    // machine (named pipes under \\.\pipe\ are never network-reachable regardless of this ACL).
    private static PipeSecurity CreatePipeSecurity()
    {
      var security = new PipeSecurity();
      var localUsers = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
      security.AddAccessRule(new PipeAccessRule(localUsers, PipeAccessRights.ReadWrite, AccessControlType.Allow));
      return security;
    }

    public void Start()
    {
      if (_acceptLoop != null) return;
      _startedUtc = DateTime.UtcNow;
      _cts = new CancellationTokenSource();
      _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
      if (_acceptLoop == null) return;

      try
      {
        _cts.Cancel();
        _acceptLoop.Wait(TimeSpan.FromSeconds(2));
      }
      catch (Exception ex)
      {
        RecordRequestError(ex);
      }

      _cts.Dispose();
      _cts = null;
      _acceptLoop = null;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
      while (!token.IsCancellationRequested)
      {
        NamedPipeServerStream pipe;
        try
        {
          pipe = NamedPipeServerStreamAcl.Create(
              _pipeName,
              PipeDirection.InOut,
              NamedPipeServerStream.MaxAllowedServerInstances,
              PipeTransmissionMode.Byte,
              PipeOptions.Asynchronous,
              inBufferSize: 4096,
              outBufferSize: 4096,
              pipeSecurity: CreatePipeSecurity());
        }
        catch (Exception ex)
        {
          // A transient failure creating the next listening instance must not take down the whole
          // server -- that would silently strand it after exactly one successful connection. Retry
          // instead of returning.
          RecordRequestError(ex);
          try
          {
            await Task.Delay(200, token).ConfigureAwait(false);
          }
          catch (OperationCanceledException)
          {
            return;
          }

          continue;
        }

        try
        {
          await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          pipe.Dispose();
          return;
        }
        catch (Exception ex)
        {
          RecordRequestError(ex);
          pipe.Dispose();
          continue;
        }

        _ = HandleClientAsync(pipe, token);
      }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
      try
      {
        using (pipe)
        using (var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, System.Text.Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" })
        {
          var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
          if (string.IsNullOrWhiteSpace(line)) return;

          var responseJson = Dispatch(line);
          await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
        }
      }
      catch (Exception ex)
      {
        RecordRequestError(ex);
      }
    }

    private string Dispatch(string requestLine)
    {
      object id = null;
      try
      {
        using var document = JsonDocument.Parse(requestLine);
        var root = document.RootElement;
        id = root.TryGetProperty("id", out var idElement) ? AddinJson.ToClrValue(idElement) : null;
        var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
        var hasParams = root.TryGetProperty("params", out var paramsElement);

        switch (method)
        {
          case "list_tools":
            return AddinJson.Serialize(new
            {
              id,
              result = new
              {
                tools = _tools.Values.Select(ToToolDescriptor).ToArray(),
                serverName = _options.ServerName,
                serverVersion = _options.ServerVersion
              }
            });

          case "call_tool":
            {
              if (!hasParams
                  || !paramsElement.TryGetProperty("name", out var nameElement)
                  || nameElement.ValueKind != JsonValueKind.String)
              {
                return AddinJson.Serialize(new { id, error = new { message = "call_tool requires params.name." } });
              }

              var toolName = nameElement.GetString();
              var arguments = paramsElement.TryGetProperty("arguments", out var argsElement)
                  ? (IDictionary<string, object>)(AddinJson.ToClrValue(argsElement) as Dictionary<string, object> ?? new Dictionary<string, object>())
                  : new Dictionary<string, object>();

              return AddinJson.Serialize(new { id, result = CallTool(toolName, arguments) });
            }

          default:
            return AddinJson.Serialize(new { id, error = new { message = $"Unknown method '{method}'." } });
        }
      }
      catch (Exception ex)
      {
        RecordRequestError(ex);
        return AddinJson.Serialize(new { id, error = new { message = ex.Message } });
      }
    }

    private object CallTool(string toolName, IDictionary<string, object> arguments)
    {
      if (string.IsNullOrWhiteSpace(toolName) || !_tools.TryGetValue(toolName, out var tool))
      {
        return new { isError = true, text = $"Unknown tool '{toolName}'.", structuredContent = (object)null };
      }

      var inputSchema = tool.InputSchema ?? AddinMcpSchema.EmptySchema();
      var argElements = arguments.ToDictionary(
          kv => kv.Key,
          kv => AddinJson.SerializeToElement(kv.Value),
          StringComparer.Ordinal);

      if (!AddinMcpArguments.TryValidateAndNormalize(tool.Name, inputSchema, argElements, out var normalized, out var validationError))
      {
        return new { isError = true, text = $"Invalid arguments for tool '{tool.Name}': {validationError}", structuredContent = (object)null };
      }

      AddinMcpToolResult toolResult;
      try
      {
        toolResult = tool.Handler?.Invoke(normalized) ?? AddinMcpToolResult.Success(new Dictionary<string, object>());
      }
      catch (Exception ex)
      {
        toolResult = AddinMcpToolResult.Error(new Dictionary<string, object>
        {
          ["ok"] = false,
          ["error"] = ex.Message,
          ["tool"] = tool.Name
        });
      }

      var structured = toolResult.StructuredContent ?? new Dictionary<string, object>();
      var text = !string.IsNullOrWhiteSpace(toolResult.Text) ? toolResult.Text : AddinJson.Serialize(structured);

      return new { isError = toolResult.IsError, text, structuredContent = structured };
    }

    private static object ToToolDescriptor(AddinMcpTool tool) => new
    {
      name = tool.Name,
      description = tool.Description ?? string.Empty,
      inputSchema = tool.InputSchema ?? AddinMcpSchema.EmptySchema()
    };

    private void RegisterTool(AddinMcpTool tool)
    {
      if (tool == null || string.IsNullOrWhiteSpace(tool.Name) || tool.Handler == null)
      {
        return;
      }

      _tools[tool.Name] = tool;
    }

    private IDictionary<string, object> GetStatusPayload(Func<IDictionary<string, object>> statusProvider)
    {
      var payload = new Dictionary<string, object>
      {
        ["isProcessRunning"] = true,
        ["server"] = _options.ServerName,
        ["version"] = _options.ServerVersion,
        ["startedUtc"] = _startedUtc.ToString("o"),
        ["nowUtc"] = DateTime.UtcNow.ToString("o"),
        ["lastRequestError"] = _lastRequestError
      };

      if (statusProvider != null)
      {
        try
        {
          var extra = statusProvider();
          if (extra != null)
          {
            foreach (var kv in extra) payload[kv.Key] = kv.Value;
          }
        }
        catch (Exception ex)
        {
          payload["statusProviderError"] = ex.Message;
        }
      }

      return payload;
    }

    private void RecordRequestError(Exception ex)
    {
      _lastRequestError = $"{DateTime.UtcNow:o} {ex.GetType().Name}: {ex.Message}";
    }

    public void Dispose()
    {
      Stop();
    }
  }
}
