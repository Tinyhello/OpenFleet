using OpenFleet.Contracts;
using UnityEngine;
using Colors = OpenFleet.Unity.PresentationController.Colors;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 一台車的顯示。只保存「最後套用的狀態」，不自行模擬；位置在兩次更新之間平滑插值。
    /// 掛在 AGV Prefab 的根物件上（Prefab 由選單 OpenFleet/Build Factory Scene 產生，執行時由 PresentationController 從 Resources 載入）。
    /// 燈條、標籤、貨物用固定的名稱在子物件裡找，不需要在 Inspector 設定。
    /// </summary>
    public sealed class VehicleView : MonoBehaviour
    {
        /// <summary>依連線狀態變色的部位（燈條、警示燈）都放在這個子物件底下。材質需開啟 Emission。</summary>
        public const string StatusLightsName = "Status Lights";
        public const string LabelName = "Label";

        /// <summary>車上有貨時顯示的物件；不載貨的車型沒有這個子物件。</summary>
        public const string LoadName = "Load";

        private const float PositionSmoothing = 12f;
        private const float RotationSmoothing = 10f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private Renderer[] _statusRenderers = new Renderer[0];
        private TextMesh _label;
        private GameObject _loadVisual;
        private MaterialPropertyBlock _block;
        private Vector3 _target;
        private Quaternion _targetRotation;

        public VehicleState State { get; private set; }

        /// <summary>建立時使用的車型；後端回報的車型不同時，FleetView 會換一個 Prefab。</summary>
        public VehicleType VehicleType { get; private set; }

        public static VehicleView Create(VehicleView prefab, string vehicleId, VehicleType type, Transform parent)
        {
            var view = Instantiate(prefab, parent);
            view.name = vehicleId;
            view.VehicleType = type;
            return view;
        }

        private void Awake()
        {
            var lights = transform.Find(StatusLightsName);
            if (lights != null)
            {
                _statusRenderers = lights.GetComponentsInChildren<Renderer>(true);
            }

            var label = transform.Find(LabelName);
            _label = label != null ? label.GetComponent<TextMesh>() : null;
            var load = transform.Find(LoadName);
            _loadVisual = load != null ? load.gameObject : null;
        }

        /// <summary>
        /// 套用狀態；同一個 StateEpoch 內，比目前 Version 舊或相同的更新會被丟棄（HTTP 快照與 MQTT 可能以任意順序到達）。
        /// Epoch 不同代表後端重啟、Version 重新計算，直接採用新狀態。
        /// </summary>
        public bool Apply(VehicleState state)
        {
            if (State != null && state.StateEpoch == State.StateEpoch && state.Version <= State.Version)
            {
                return false;
            }

            // 後端座標以公尺為單位、Y 朝上，與 Unity 相同，直接對應。Prefab 的原點在車底中心。
            var position = new Vector3((float)state.Position.X, (float)state.Position.Y, (float)state.Position.Z);
            if (State == null)
            {
                transform.position = position;
                _targetRotation = transform.rotation;
            }
            else
            {
                var delta = position - _target;
                delta.y = 0f;
                if (delta.sqrMagnitude > 0.0001f)
                {
                    _targetRotation = Quaternion.LookRotation(delta);
                }
            }

            _target = position;
            var connectivityChanged = State == null || State.Connectivity != state.Connectivity;
            var loadChanged = State == null || State.LoadQuantity != state.LoadQuantity;
            State = state;

            if (connectivityChanged)
            {
                ShowConnectivity(state);
            }

            if (connectivityChanged || loadChanged)
            {
                ShowLoad(state);
            }

            return true;
        }

        /// <summary>空車或載貨：車上的貨物物件與標籤文字。只切換顯示，沒有動畫。</summary>
        private void ShowLoad(VehicleState state)
        {
            var online = state.Connectivity == VehicleConnectivity.Online;
            if (_loadVisual != null)
            {
                _loadVisual.SetActive(state.LoadQuantity > 0);
            }

            if (_label != null)
            {
                var load = state.LoadQuantity <= 0 ? "空車"
                    : state.LoadCode == LoadCodes.Tool ? "載刀具"
                    : $"載 {state.LoadQuantity} 件";
                _label.text = online ? $"{state.VehicleId}\n{load}" : $"{state.VehicleId}\nOFFLINE";
                _label.color = online ? Color.white : Colors.Offline;
            }
        }

        private void ShowConnectivity(VehicleState state)
        {
            var online = state.Connectivity == VehicleConnectivity.Online;
            var color = online ? Colors.Online : Colors.Offline;

            if (_block == null)
            {
                _block = new MaterialPropertyBlock();
            }

            _block.SetColor(BaseColorId, color);
            _block.SetColor(EmissionColorId, online ? color * Colors.StatusEmission : Color.black);
            foreach (var r in _statusRenderers)
            {
                r.SetPropertyBlock(_block);
            }
        }

        private void Update()
        {
            if (State == null)
            {
                return;
            }

            var t = 1f - Mathf.Exp(-PositionSmoothing * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, _target, t);
            var r = 1f - Mathf.Exp(-RotationSmoothing * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, r);
        }
    }
}
