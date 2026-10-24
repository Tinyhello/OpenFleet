using System.Collections.Generic;
using System.Linq;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>
    /// 模組的顯示名稱。站名規則（第 4 節）：同一道工序只有一台時用工序名稱（「淬火」），
    /// 不只一台時加編號與量詞（「粗加工 2 號機」）。倉儲站與裝飾物用目錄的名稱；AGV 車位加車號（「大型 AGV agv-04」）。
    /// </summary>
    public static class ModuleNaming
    {
        public static string DisplayName(FactoryLayoutDocument document, LayoutModule module)
        {
            var spec = ModuleCatalog.Get(module.Kind);
            if (spec.ProcessCode != null)
            {
                var same = SameKind(document, module.Kind);
                if (same.Count <= 1)
                {
                    return spec.ZhName;
                }

                return spec.ZhName + " " + NumberOf(module, same) + " " + spec.Unit;
            }

            if (ModuleCatalog.IsBay(module.Kind))
            {
                return spec.ZhName + " " + module.Code;
            }

            return spec.ZhName;
        }

        public static string EnglishName(FactoryLayoutDocument document, LayoutModule module)
        {
            var spec = ModuleCatalog.Get(module.Kind);
            if (spec.ProcessCode != null)
            {
                var same = SameKind(document, module.Kind);
                if (same.Count <= 1)
                {
                    return spec.EnName;
                }

                return spec.EnName + " #" + NumberOf(module, same);
            }

            if (ModuleCatalog.IsBay(module.Kind))
            {
                return spec.EnName + " " + module.Code;
            }

            return spec.EnName;
        }

        // 代碼的數字部分（rough-2 → 2）；代碼沒有數字時依文件裡的順序編號。
        private static int NumberOf(LayoutModule module, List<LayoutModule> same)
        {
            var dash = module.Code.LastIndexOf('-');
            if (dash >= 0 && int.TryParse(module.Code.Substring(dash + 1), out var number) && number > 0)
            {
                return number;
            }

            return same.IndexOf(module) + 1;
        }

        private static List<LayoutModule> SameKind(FactoryLayoutDocument document, ModuleKind kind) =>
            document.Modules.Where(m => m.Kind == kind).ToList();
    }
}
