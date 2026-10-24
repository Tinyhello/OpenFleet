using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Layout;

/// <summary>佈局文件的 JSON 格式：資料庫的 jsonb 欄位與雜湊共用同一份設定，雜湊才會穩定。</summary>
public static class LayoutJson
{
    public static string Serialize(FactoryLayoutDocument document) => JsonSerializer.Serialize(document, JsonDefaults.Options);

    public static FactoryLayoutDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<FactoryLayoutDocument>(json, JsonDefaults.Options) ?? new FactoryLayoutDocument();

    /// <summary>文件 JSON 的 SHA-256（小寫十六進位）。套用時記下來，啟動時比對預設佈局的程式有沒有改。</summary>
    public static string HashOf(FactoryLayoutDocument document) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(document)))).ToLowerInvariant();
}
