using UnityEngine;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 場景中的站點與後端站點代碼的對應。執行時由 PresentationController 加到每個站點的根物件上：
    /// 物件名稱就是站點代碼，備料區、成品區的物件用固定的名稱在子物件裡找，不需要在 Inspector 設定。
    /// StatusOverlay 依代碼找到這裡，把進度條畫在機台上，並依數量顯示或隱藏備料區、成品區的物件。
    /// </summary>
    public sealed class StationAnchor : MonoBehaviour
    {
        /// <summary>備料區、成品區有料時顯示的物件名稱；Build Factory Scene 組模型時用同一組常數。沒有這兩區的站點不會有這些物件。</summary>
        public const string InboundContentsName = "Inbound Contents";
        public const string OutboundContentsName = "Outbound Contents";

        /// <summary>進度條在站點區域座標中的位置：站點後側（靠牆），不擋住機台前方的備料區、成品區與交接點。</summary>
        private static readonly Vector3 OverlayOffset = new Vector3(0f, 0.3f, -2.1f);

        private GameObject _inboundContents;
        private GameObject _outboundContents;

        /// <summary>必須和後端的站點代碼相同，StatusOverlay 才對得上。</summary>
        public string StationCode => name;

        public Vector3 OverlayPosition => transform.TransformPoint(OverlayOffset);

        private void Awake()
        {
            _inboundContents = FindChild(InboundContentsName);
            _outboundContents = FindChild(OutboundContentsName);
        }

        /// <summary>只切換顯示，沒有動畫；數量由進度條表示。</summary>
        public void ShowContents(bool hasInbound, bool hasOutbound)
        {
            if (_inboundContents != null && _inboundContents.activeSelf != hasInbound)
            {
                _inboundContents.SetActive(hasInbound);
            }

            if (_outboundContents != null && _outboundContents.activeSelf != hasOutbound)
            {
                _outboundContents.SetActive(hasOutbound);
            }
        }

        private GameObject FindChild(string childName)
        {
            foreach (var child in GetComponentsInChildren<Transform>(true))
            {
                if (child.name == childName)
                {
                    return child.gameObject;
                }
            }

            return null;
        }
    }
}
