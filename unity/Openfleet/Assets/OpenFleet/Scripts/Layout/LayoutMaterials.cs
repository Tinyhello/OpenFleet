using UnityEngine;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 執行時建立環境與路網要用的材質清單。資產在 Resources/Layout/LayoutMaterials.asset，
    /// 由選單 OpenFleet/Build Factory Scene 依 Palette 填入（不手動設定）；執行時由 PresentationController 載入。
    /// 放在 Resources 才會被 Player 建置包含：執行時用 Shader.Find 建立的材質可能被裁掉。
    /// </summary>
    public sealed class LayoutMaterials : ScriptableObject
    {
        public Material Floor;
        public Material Wall;
        public Material Pillar;
        public Material Aisle;
        public Material LaneLine;
        public Material ZoneLine;
        public Material Handoff;
        public Material Pallet;
        public Material Cardboard;
        public Material Steel;
        public Material MachineBody;

        /// <summary>有欄位沒填（資產過期）時回傳 false；呼叫端要提示重新執行 Build Factory Scene。</summary>
        public bool IsComplete =>
            Floor != null && Wall != null && Pillar != null && Aisle != null && LaneLine != null && ZoneLine != null
            && Handoff != null && Pallet != null && Cardboard != null && Steel != null && MachineBody != null;
    }
}
