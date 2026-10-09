using OpenFleet.Contracts;
using UnityEngine;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 一台車的顯示。只保存「最後套用的狀態」，不自行模擬；位置在兩次更新之間平滑插值。
    /// 掛在 AGV Prefab 的根物件上（Prefab 由選單 OpenFleet/Build Factory Scene 產生）。
    /// </summary>
    public sealed class VehicleView : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        [SerializeField] private VehicleType vehicleType;

        [Tooltip("依連線狀態變色的部位（燈條、警示燈）。材質需開啟 Emission。")]
        [SerializeField] private Renderer[] statusRenderers = new Renderer[0];

        [SerializeField] private TextMesh label;
        [SerializeField] private float statusEmission = 2f;
        [SerializeField] private float positionSmoothing = 12f;
        [SerializeField] private float rotationSmoothing = 10f;

        private MaterialPropertyBlock _block;
        private Vector3 _target;
        private Quaternion _targetRotation;

        public VehicleState State { get; private set; }

        /// <summary>這個 Prefab 代表的車型；後端回報的車型不同時，FleetView 會換一個 Prefab。</summary>
        public VehicleType VehicleType => vehicleType;

        public static VehicleView Create(VehicleView prefab, string vehicleId, Transform parent)
        {
            var view = Instantiate(prefab, parent);
            view.name = vehicleId;
            return view;
        }

        /// <summary>
        /// 套用狀態；同一個 StateEpoch 內，比目前 Version 舊或相同的更新會被丟棄（HTTP 快照與 MQTT 可能以任意順序到達）。
        /// Epoch 不同代表後端重啟、Version 重新計算，直接採用新狀態。
        /// </summary>
        public bool Apply(VehicleState state, Color onlineColor, Color offlineColor)
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
            State = state;

            if (connectivityChanged)
            {
                ShowConnectivity(state, onlineColor, offlineColor);
            }

            return true;
        }

        private void ShowConnectivity(VehicleState state, Color onlineColor, Color offlineColor)
        {
            var online = state.Connectivity == VehicleConnectivity.Online;
            var color = online ? onlineColor : offlineColor;

            if (_block == null)
            {
                _block = new MaterialPropertyBlock();
            }

            _block.SetColor(BaseColorId, color);
            _block.SetColor(EmissionColorId, online ? color * statusEmission : Color.black);
            foreach (var r in statusRenderers)
            {
                r.SetPropertyBlock(_block);
            }

            if (label != null)
            {
                label.text = online ? state.VehicleId : state.VehicleId + "\nOFFLINE";
                label.color = online ? Color.white : offlineColor;
            }
        }

        private void Update()
        {
            if (State == null)
            {
                return;
            }

            var t = 1f - Mathf.Exp(-positionSmoothing * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, _target, t);
            var r = 1f - Mathf.Exp(-rotationSmoothing * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, r);
        }
    }
}
