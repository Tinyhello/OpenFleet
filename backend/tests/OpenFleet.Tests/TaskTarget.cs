using OpenFleet.Api.Data;

namespace OpenFleet.Tests;

/// <summary>
/// 搬運的目的地，給斷言用（P7 第 1a 步）：已經派車的是站代碼；還在待派工、只指定到工序的，是工序代碼。
/// 1a 的工序代碼和站代碼相同（每道工序一站），所以既有的期望值字串不變。
/// </summary>
public static class TaskTarget
{
    // 示範製程的工序序號 → 工序代碼（與 DatabaseInitializer.DemoSteps 相同）。
    private static readonly Dictionary<int, string> StepCodes = new()
    {
        [0] = "cutting",
        [1] = "rough",
        [2] = "fine",
        [3] = "heat",
        [4] = "temper",
        [5] = "plating",
        [6] = "polish",
        [7] = "qc",
        [8] = "packing",
    };

    public static string Of(TransportTask task) => task.ToStationCode ?? StepCodes[task.ToStepSequence!.Value];
}
