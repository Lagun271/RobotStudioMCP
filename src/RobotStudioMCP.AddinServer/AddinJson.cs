using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobotStudioMCP.AddinServer
{
  /// <summary>
  /// System.Text.Json replacement for the .NET Framework JavaScriptSerializer (not available on .NET 10).
  /// <see cref="DeserializeObject"/> keeps the JavaScriptSerializer value shapes that tool handlers and argument
  /// coercion rely on: objects become <c>Dictionary&lt;string, object&gt;</c>, arrays <c>object[]</c>, and numbers
  /// <c>int</c>, then <c>long</c>, then <c>decimal</c>, then <c>double</c>.
  /// </summary>
  public static class AddinJson
  {
    private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
    {
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly JsonSerializerOptions ReadDtoOptions = new JsonSerializerOptions
    {
      PropertyNameCaseInsensitive = true,
      NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static string Serialize(object value)
        => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), WriteOptions);

    public static object DeserializeObject(string json)
    {
      if (string.IsNullOrWhiteSpace(json))
      {
        return null;
      }

      using (var document = JsonDocument.Parse(json))
      {
        return ToClrValue(document.RootElement);
      }
    }

    public static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, ReadDtoOptions);

    public static JsonElement SerializeToElement(object value)
        => JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), WriteOptions);

    /// <summary>
    /// Converts a parsed <see cref="JsonElement"/> into the same CLR value shapes <see cref="DeserializeObject"/>
    /// produces (objects to <c>Dictionary&lt;string, object&gt;</c>, arrays to <c>object[]</c>, numbers to
    /// <c>int</c>/<c>long</c>/<c>decimal</c>/<c>double</c>), so tool argument handlers see one consistent shape
    /// regardless of whether the value came from the raw HTTP body or from the MCP SDK's typed arguments.
    /// </summary>
    public static object ToClrValue(JsonElement element)
    {
      switch (element.ValueKind)
      {
        case JsonValueKind.Object:
          var dictionary = new Dictionary<string, object>();
          foreach (var property in element.EnumerateObject())
          {
            dictionary[property.Name] = ToClrValue(property.Value);
          }
          return dictionary;

        case JsonValueKind.Array:
          return element.EnumerateArray().Select(ToClrValue).ToArray();

        case JsonValueKind.String:
          return element.GetString();

        case JsonValueKind.Number:
          if (element.TryGetInt32(out var intValue)) return intValue;
          if (element.TryGetInt64(out var longValue)) return longValue;
          if (element.TryGetDecimal(out var decimalValue)) return decimalValue;
          return element.GetDouble();

        case JsonValueKind.True:
          return true;

        case JsonValueKind.False:
          return false;

        default:
          return null;
      }
    }
  }
}
