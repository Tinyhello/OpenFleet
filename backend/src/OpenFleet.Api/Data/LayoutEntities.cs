using OpenFleet.Contracts;

namespace OpenFleet.Api.Data;

/// <summary>
/// 一份工廠佈局（P7 第 2 步）。Document 是 jsonb。同時只有一份是使用中（IsActive），
/// 使用中的那份推導出來的資料寫在 Stations、Machines、RouteNodes、Vehicles 等資料表。
/// 預設佈局（IsDefault）由程式產生，不能修改、不能刪除。
/// </summary>
public sealed class FactoryLayoutEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public FactoryLayoutDocument Document { get; set; } = new();
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>最後一次套用的時間；從來沒套用過是 null。</summary>
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>套用當時文件 JSON 的雜湊。啟動時和目前的文件比對：預設佈局不同，代表程式改過它，要重新套用。</summary>
    public string? AppliedHash { get; set; }

    /// <summary>
    /// 套用當時實際驗證並推導的那份文件（快照）。使用中的佈局存檔不會自動套用，所以 Document 可能比工廠正在跑的新；
    /// 場景、啟動檢查都以這份為準（使用者決定，2026-10-25）。從來沒套用過是 null。
    /// </summary>
    public FactoryLayoutDocument? AppliedDocument { get; set; }
}
