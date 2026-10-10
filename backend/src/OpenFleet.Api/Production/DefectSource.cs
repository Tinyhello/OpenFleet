using OpenFleet.Contracts;

namespace OpenFleet.Api.Production;

/// <summary>
/// 品管檢驗每一支的結果從哪裡來。模擬裡沒有真正的量測，所以用機率產生；
/// 抽成介面是為了讓測試可以指定「哪一支是什麼不良」，不靠亂數。
/// </summary>
public interface IDefectSource
{
    /// <summary>檢驗一支：合格回傳 null，不良回傳原因。<paramref name="defectRate"/> 是不良率（0～1）。</summary>
    DefectReason? Inspect(double defectRate);
}

/// <summary>
/// 依不良率隨機產生不良，三種原因機率相同（使用者 2026-10-10：不良率 10%，各原因先平均分）。
/// </summary>
public sealed class RandomDefectSource : IDefectSource
{
    private static readonly DefectReason[] Reasons = Enum.GetValues<DefectReason>();

    public DefectReason? Inspect(double defectRate) =>
        Random.Shared.NextDouble() < defectRate ? Reasons[Random.Shared.Next(Reasons.Length)] : null;
}
