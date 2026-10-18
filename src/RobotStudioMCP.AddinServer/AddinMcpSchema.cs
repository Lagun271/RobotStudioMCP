using System.Collections.Generic;

namespace RobotStudioMCP.AddinServer
{
  public static class AddinMcpSchema
  {
    public static IDictionary<string, object> EmptySchema()
        => BuildSchema(new Dictionary<string, object>());

    public static IDictionary<string, object> BuildSchema(IDictionary<string, object> properties, params string[] required)
    {
      var schema = new Dictionary<string, object>
      {
        ["type"] = "object",
        ["properties"] = properties ?? new Dictionary<string, object>(),
        ["additionalProperties"] = false
      };

      if (required != null && required.Length > 0)
      {
        schema["required"] = required;
      }

      return schema;
    }

    public static IDictionary<string, object> StringProperty(string description)
        => new Dictionary<string, object>
        {
          ["type"] = "string",
          ["description"] = description
        };

    public static IDictionary<string, object> BoolProperty(string description)
        => new Dictionary<string, object>
        {
          ["type"] = "boolean",
          ["description"] = description
        };
  }
}
