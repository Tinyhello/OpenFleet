using OpenFleet.Api.Traffic;
using OpenFleet.Contracts;
using OpenFleet.Layout;

namespace OpenFleet.Tests;

/// <summary>
/// 測試用的佈局。預設佈局把車位改名成既有測試用的車（agv-a、agv-b、agv-c 是大型，agv-s 是小型），拿掉 agv-05。
/// 車位的位置不變，所以路網與站點和正式的預設佈局完全相同；測試只是車號不同。
/// 這些名稱不符合正式的代碼規則（agv-NN），所以只用來直接寫入資料庫，不經過驗證（驗證與套用的測試用 agv-NN 的佈局）。
/// </summary>
public static class TestLayouts
{
    private static readonly Dictionary<string, string> Renames = new(StringComparer.Ordinal)
    {
        ["agv-01"] = "agv-a",
        ["agv-02"] = "agv-b",
        ["agv-04"] = "agv-c",
        ["agv-03"] = "agv-s",
    };

    public static FactoryLayoutDocument Default()
    {
        var document = DefaultLayout.Build();
        document.Modules.RemoveAll(m => m.Code == "agv-05");
        foreach (var module in document.Modules)
        {
            if (Renames.TryGetValue(module.Code, out var renamed))
            {
                module.Code = renamed;
            }
        }

        return document;
    }

    /// <summary>照佈局的路網建立路網（和 DatabaseInitializer 寫入資料庫的是同一份）。</summary>
    public static RoadNetwork Network(FactoryLayoutDocument document) =>
        new(document.RoadNodes.Select(n => new RoadNode(n.Code, n.X, n.Z)), document.RoadEdges.Select(e => (e.From, e.To)));
}
