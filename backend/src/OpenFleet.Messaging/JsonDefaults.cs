using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFleet.Messaging;

/// <summary>
/// MQTT 與 HTTP 共用的 JSON 規則：camelCase 欄位、enum 以字串表示。
/// Unity 端使用 Newtonsoft 時需設定相同規則（CamelCasePropertyNamesContractResolver + StringEnumConverter）。
/// </summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
