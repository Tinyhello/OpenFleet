using OpenFleet.Contracts;

namespace OpenFleet.Api.Data;

/// <summary>
/// 缺車紀錄共用的時間欄位。起訖是實際時鐘；<see cref="SimulatedSeconds"/> 是換算成模擬時間的長度
/// （每次檢查時加上「經過的實際秒數 × 當時的時間倍率」），統計用這個值，倍率中途改變也不會失真。
/// </summary>
public abstract class ShortageInterval
{
    public long Id { get; set; }

    /// <summary>
    /// 叫不到車的原因。車都在忙（缺車，車不夠用）與該車型全部離線（重大事故）是兩回事，分開記錄、分開統計；
    /// 原因改變時結束這一筆、另開一筆。
    /// </summary>
    public ShortageReason Reason { get; protected set; }
    public DateTimeOffset StartedAt { get; protected set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public double SimulatedSeconds { get; private set; }

    /// <summary>已經累計到哪個時間點。</summary>
    public DateTimeOffset AccruedAt { get; protected set; }

    public bool IsOpen => EndedAt is null;

    public void Accrue(DateTimeOffset now, double timeScale)
    {
        if (now > AccruedAt)
        {
            SimulatedSeconds += (now - AccruedAt).TotalSeconds * timeScale;
            AccruedAt = now;
        }
    }

    public void Close(DateTimeOffset now) => EndedAt = now;
}

/// <summary>
/// 等車區間：某個搬運任務從「叫不到車」起，到派出車為止（或原因改變為止）。
/// 目的站放不下而等待的任務不算（那是容量問題）；派出之後的行駛與交通等待也不算。
/// </summary>
public sealed class VehicleWait : ShortageInterval
{
    public Guid TaskId { get; private set; }

    public static VehicleWait Open(Guid taskId, ShortageReason reason, DateTimeOffset now) =>
        new() { TaskId = taskId, Reason = reason, StartedAt = now, AccruedAt = now };
}

/// <summary>
/// 機台因為叫不到車而停下來的區間（耽誤的加工時間），按工序統計。
/// 只在機台真的沒有在加工、也不是在換刀時才算；同一台機台同一時間只有一筆，不會因為有兩個任務在等就重複計算。
/// </summary>
public sealed class MachineDelay : ShortageInterval
{
    public string StationCode { get; private set; } = string.Empty;
    public int StepSequence { get; private set; }
    public MachineDelayCause Cause { get; private set; }

    /// <summary>造成停機、正在等車的那個搬運任務。</summary>
    public Guid TaskId { get; private set; }

    public static MachineDelay Open(
        string stationCode, int stepSequence, MachineDelayCause cause, ShortageReason reason, Guid taskId, DateTimeOffset now) => new()
    {
        StationCode = stationCode,
        StepSequence = stepSequence,
        Cause = cause,
        Reason = reason,
        TaskId = taskId,
        StartedAt = now,
        AccruedAt = now,
    };
}
