using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

namespace RobotStudioMCP.AddinServer
{
  /// <summary>
  /// Validates and normalizes <c>tools/call</c> arguments against an <see cref="AddinMcpTool.InputSchema"/>.
  /// Unlike the ad-hoc coercion this replaces, a value whose JSON kind doesn't match the declared schema
  /// type is rejected rather than parsed from its string form (so a schema-"integer" argument sent as the
  /// JSON string "42" is an error, not a silent conversion).
  /// </summary>
  internal static class AddinMcpArguments
  {
    public static bool TryValidateAndNormalize(
        string toolName,
        IDictionary<string, object> inputSchema,
        IDictionary<string, JsonElement> arguments,
        out Dictionary<string, object> normalized,
        out string error)
    {
      normalized = new Dictionary<string, object>(StringComparer.Ordinal);
      error = null;
      arguments ??= new Dictionary<string, JsonElement>();

      var properties = GetSchemaProperties(inputSchema);
      var required = GetRequiredFields(inputSchema);

      foreach (var req in required)
      {
        if (!arguments.ContainsKey(req))
        {
          error = $"Missing required argument '{req}'.";
          return false;
        }
      }

      foreach (var kv in arguments)
      {
        if (!properties.TryGetValue(kv.Key, out var propSchema))
        {
          error = $"Unknown argument '{kv.Key}'.";
          return false;
        }

        if (!TryCoerce(kv.Key, propSchema, kv.Value, out var coerced, out var coerceError))
        {
          error = $"Argument '{kv.Key}' {coerceError}";
          return false;
        }

        normalized[kv.Key] = coerced;
      }

      return true;
    }

    private static bool TryCoerce(
        string argumentName,
        IDictionary<string, object> propertySchema,
        JsonElement value,
        out object coerced,
        out string error)
    {
      coerced = null;
      error = null;

      var expectedType = propertySchema != null && propertySchema.TryGetValue("type", out var typeObj)
          ? typeObj?.ToString()
          : null;

      if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
      {
        coerced = null;
        return true;
      }

      if (string.IsNullOrWhiteSpace(expectedType))
      {
        coerced = AddinJson.ToClrValue(value);
        return true;
      }

      switch (expectedType.Trim().ToLowerInvariant())
      {
        case "string":
          if (value.ValueKind != JsonValueKind.String)
          {
            error = "must be a string.";
            return false;
          }
          coerced = value.GetString();
          return true;

        case "boolean":
          if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
          {
            error = "must be a boolean.";
            return false;
          }
          coerced = value.GetBoolean();
          return true;

        case "integer":
          if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var longValue)
              || longValue < int.MinValue || longValue > int.MaxValue)
          {
            error = "must be an integer.";
            return false;
          }
          coerced = (int)longValue;
          return true;

        case "number":
          if (value.ValueKind != JsonValueKind.Number)
          {
            error = "must be a number.";
            return false;
          }
          coerced = value.GetDouble();
          return true;

        case "object":
          if (value.ValueKind != JsonValueKind.Object)
          {
            error = "must be an object.";
            return false;
          }
          coerced = AddinJson.ToClrValue(value);
          return true;

        case "array":
          if (value.ValueKind != JsonValueKind.Array)
          {
            error = "must be an array.";
            return false;
          }
          return TryCoerceArray(argumentName, propertySchema, value, out coerced, out error);

        default:
          coerced = AddinJson.ToClrValue(value);
          return true;
      }
    }

    private static bool TryCoerceArray(
        string argumentName,
        IDictionary<string, object> arraySchema,
        JsonElement value,
        out object coerced,
        out string error)
    {
      coerced = null;
      error = null;

      var itemsSchema = arraySchema != null && arraySchema.TryGetValue("items", out var itemsObj)
          ? itemsObj as IDictionary
          : null;
      var itemsSchemaDict = itemsSchema != null ? ToDictionary(itemsSchema) : null;

      var items = new List<object>();
      var index = 0;
      foreach (var item in value.EnumerateArray())
      {
        if (itemsSchemaDict != null)
        {
          if (!TryCoerce($"{argumentName}[{index}]", itemsSchemaDict, item, out var itemCoerced, out var itemError))
          {
            error = $"item {index} {itemError}";
            return false;
          }
          items.Add(itemCoerced);
        }
        else
        {
          items.Add(AddinJson.ToClrValue(item));
        }

        index++;
      }

      coerced = items.ToArray();
      return true;
    }

    private static Dictionary<string, IDictionary<string, object>> GetSchemaProperties(IDictionary<string, object> schema)
    {
      var result = new Dictionary<string, IDictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
      if (schema == null || !schema.TryGetValue("properties", out var propsObj) || propsObj == null)
      {
        return result;
      }

      if (!(propsObj is IDictionary propsDict))
      {
        return result;
      }

      foreach (DictionaryEntry entry in propsDict)
      {
        if (entry.Key == null) continue;
        var name = entry.Key.ToString();
        var valueDict = entry.Value as IDictionary;
        result[name] = valueDict != null
            ? ToDictionary(valueDict)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
      }

      return result;
    }

    private static HashSet<string> GetRequiredFields(IDictionary<string, object> schema)
    {
      var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (schema == null || !schema.TryGetValue("required", out var requiredObj) || requiredObj == null)
      {
        return result;
      }

      if (requiredObj is IEnumerable requiredEnumerable)
      {
        foreach (var item in requiredEnumerable)
        {
          var name = item?.ToString();
          if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
        }
      }

      return result;
    }

    private static Dictionary<string, object> ToDictionary(IDictionary source)
    {
      var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
      if (source == null) return result;

      foreach (DictionaryEntry entry in source)
      {
        if (entry.Key == null) continue;
        result[entry.Key.ToString()] = entry.Value;
      }

      return result;
    }
  }
}
