using System.Collections.Generic;
using System.Globalization;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>
    /// 新增模組時產生代碼（規劃第 7 節）：製程機台 {工序代碼}-{n}、AGV 車位 agv-{nn}（至少兩位數）、
    /// 裝飾物 misc-{n}／carrier-{n}，n 取最小沒用過的正整數；倉儲站是固定代碼。產生的代碼一定符合 <see cref="ModuleCatalog.IsValidCode"/>。
    /// </summary>
    public static class ModuleCodes
    {
        /// <summary>
        /// 下一個可用的代碼。倉儲站（原料倉、成品倉、出貨、刀具庫）已經有了就回傳 null（不能多放）。
        /// 「已用」以文件裡所有模組的代碼為準，不分種類。
        /// </summary>
        public static string? Next(FactoryLayoutDocument document, ModuleKind kind)
        {
            var spec = ModuleCatalog.Get(kind);
            var used = new HashSet<string>();
            foreach (var module in document.Modules)
            {
                used.Add(module.Code);
            }

            string? code;
            if (spec.FixedCode != null)
            {
                var exists = used.Contains(spec.FixedCode);
                foreach (var module in document.Modules)
                {
                    exists |= module.Kind == kind;
                }

                code = exists ? null : spec.FixedCode;
            }
            else
            {
                string prefix;
                if (spec.ProcessCode != null)
                {
                    prefix = spec.ProcessCode + "-";
                }
                else if (ModuleCatalog.IsBay(kind))
                {
                    prefix = "agv-";
                }
                else
                {
                    prefix = kind == ModuleKind.MiscStore ? "misc-" : "carrier-";
                }

                var n = 1;
                while (true)
                {
                    var number = ModuleCatalog.IsBay(kind) ? n.ToString("00", CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture);
                    code = prefix + number;
                    if (!used.Contains(code))
                    {
                        break;
                    }

                    n++;
                }
            }

            return code != null && ModuleCatalog.IsValidCode(kind, code) ? code : null;
        }
    }
}
