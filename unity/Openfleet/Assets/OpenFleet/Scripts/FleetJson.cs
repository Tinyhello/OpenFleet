using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 與後端 OpenFleet.Messaging.JsonDefaults 相同的 JSON 規則：camelCase 欄位、enum 以 camelCase 字串表示。
    /// </summary>
    public static class FleetJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = { new StringEnumConverter(new CamelCaseNamingStrategy()) },
            // 保留時區資訊；預設會轉成 DateTime 而遺失 offset。
            DateParseHandling = DateParseHandling.DateTimeOffset,
        };

        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        public static string Serialize(object value)
        {
            return JsonConvert.SerializeObject(value, Settings);
        }
    }
}
