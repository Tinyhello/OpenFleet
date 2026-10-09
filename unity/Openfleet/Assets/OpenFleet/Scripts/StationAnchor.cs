using UnityEngine;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 場景中的站點與後端站點代碼的對應，掛在每個站點的根物件上（由 Build Factory Scene 產生）。
    /// StatusOverlay 依代碼找到這裡，把進度條畫在機台上，並依數量顯示或隱藏備料區、成品區的物件。
    /// </summary>
    public sealed class StationAnchor : MonoBehaviour
    {
        [SerializeField] private string stationCode;

        [Tooltip("進度條在站點區域座標中的位置。預設在站點後側（靠牆），不擋住機台前方的備料區、成品區與交接點。")]
        [SerializeField] private Vector3 overlayOffset = new Vector3(0f, 0.3f, -2.1f);

        [Tooltip("備料區有料時顯示的物件；沒有備料區的站點留空。")]
        [SerializeField] private GameObject inboundContents;

        [Tooltip("成品區有料時顯示的物件；沒有成品區的站點留空。")]
        [SerializeField] private GameObject outboundContents;

        public string StationCode => stationCode;

        public Vector3 OverlayPosition => transform.TransformPoint(overlayOffset);

        public void Configure(string code, GameObject inbound, GameObject outbound)
        {
            stationCode = code;
            inboundContents = inbound;
            outboundContents = outbound;
        }

        /// <summary>只切換顯示，沒有動畫；數量由進度條表示。</summary>
        public void ShowContents(bool hasInbound, bool hasOutbound)
        {
            if (inboundContents != null && inboundContents.activeSelf != hasInbound)
            {
                inboundContents.SetActive(hasInbound);
            }

            if (outboundContents != null && outboundContents.activeSelf != hasOutbound)
            {
                outboundContents.SetActive(hasOutbound);
            }
        }
    }
}
