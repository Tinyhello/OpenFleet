using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Layout;

/// <summary>佈局文件的 JSON 格式：資料庫的 jsonb 欄位與雜湊共用同一份設定，雜湊才會穩定。</summary>
public static class LayoutJson
{
    // 廠房的 outline、gate 是 null 時不寫出：舊文件與預設佈局（沿用矩形寫法）的 JSON 因此和加欄位之前完全相同，
    // 已存的雜湊與套用快照不會失效，使用中的佈局也不會被誤判成有未套用的修改。
    // 只限這兩個欄位：其他欄位是 null 代表文件壞了（驗證要看得到），不能被省略成預設值。
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type != typeof(LayoutFloor))
            {
                return;
            }

            foreach (var property in typeInfo.Properties)
            {
                if (string.Equals(property.Name, "outline", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "gate", StringComparison.OrdinalIgnoreCase))
                {
                    property.ShouldSerialize = (_, value) => value != null;
                }
            }
        });
        options.TypeInfoResolver = resolver;
        return options;
    }

    public static string Serialize(FactoryLayoutDocument document) => JsonSerializer.Serialize(document, Options);

    public static FactoryLayoutDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<FactoryLayoutDocument>(json, Options) ?? new FactoryLayoutDocument();

    /// <summary>文件 JSON 的 SHA-256（小寫十六進位）。套用時記下來，啟動時比對預設佈局的程式有沒有改。</summary>
    public static string HashOf(FactoryLayoutDocument document) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(document)))).ToLowerInvariant();
}
