using UnityEngine;
using UnityEngine.InputSystem;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 俯視工廠的攝影機：右鍵拖曳旋轉、中鍵拖曳或 WASD 平移、滾輪縮放、F 回到預設視角。
    /// 可跟隨一台車（Follow）；平移、按 Esc 或 F 取消跟隨。
    /// </summary>
    public sealed class FactoryCameraController : MonoBehaviour
    {
        /// <summary>
        /// 佈局還沒載入時的視角（Editor 建場景時也用這個值），等於預設佈局算出來的預設視角。
        /// 載入佈局後由 <see cref="SetHome"/> 依廠房大小換掉（算法在 LayoutController）。
        /// 右邊有操作面板蓋住，中心偏西到 X = 3，預設佈局的切料到精加工 2 號機都在畫面裡（第 1b 步決定）。
        /// </summary>
        public static readonly Vector3 DefaultTarget = new Vector3(3f, 0f, -1.5f);
        public const float DefaultDistance = 56f;

        /// <summary>最遠的縮放距離是預設距離的幾倍（Claude 暫定；原本固定 80，約是 56 的 1.43 倍）。</summary>
        public const float MaxDistanceFactor = 1.45f;

        [SerializeField] private Vector3 target = DefaultTarget;
        [SerializeField] private float yaw;
        [SerializeField] private float pitch = 50f;
        [SerializeField] private float distance = DefaultDistance;
        [SerializeField] private float minDistance = 5f;
        [SerializeField] private float maxDistance = 80f;
        [SerializeField] private float rotateSpeed = 0.25f;
        [SerializeField] private float keyPanSpeed = 0.6f;

        private Vector3 _homeTarget;
        private float _homeYaw;
        private float _homePitch;
        private float _homeDistance;

        public Transform Following { get; private set; }

        // Focus 的目標點；到達或使用者自己平移、跟隨、回預設視角時結束。
        private Vector3 _focusPoint;
        private bool _focusing;

        /// <summary>
        /// 把視角目標平滑移到 <paramref name="point"/>（地面座標），縮放與角度不變，並取消跟隨。
        /// 佈局編輯時點選檢查結果的問題用（P7 第 4 步）。
        /// </summary>
        public void Focus(Vector3 point)
        {
            Following = null;
            _focusPoint = new Vector3(point.x, 0f, point.z);
            _focusing = true;
        }

        public void Follow(Transform vehicle)
        {
            Following = vehicle;
            if (vehicle != null)
            {
                _focusing = false;
                distance = Mathf.Min(distance, 16f);
            }
        }

        private void Awake()
        {
            _homeTarget = target;
            _homeYaw = yaw;
            _homePitch = pitch;
            _homeDistance = distance;
        }

        /// <summary>
        /// 換預設視角（F 鍵回到的位置）：佈局載入或廠房大小改變時由 LayoutController 呼叫。
        /// 最遠的縮放距離也跟著變成預設距離的 <see cref="MaxDistanceFactor"/> 倍。
        /// <paramref name="moveCamera"/> 為 true 時攝影機也立刻回到這個視角（取消跟隨）。
        /// </summary>
        public void SetHome(Vector3 homeTarget, float homeDistance, bool moveCamera = true)
        {
            _homeTarget = homeTarget;
            _homeDistance = homeDistance;
            maxDistance = homeDistance * MaxDistanceFactor;
            if (!moveCamera)
            {
                distance = Mathf.Min(distance, maxDistance);
                return;
            }

            Following = null;
            _focusing = false;
            target = _homeTarget;
            yaw = _homeYaw;
            pitch = _homePitch;
            distance = _homeDistance;
        }

        private void Update()
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                var delta = mouse.delta.ReadValue();
                if (mouse.rightButton.isPressed)
                {
                    yaw += delta.x * rotateSpeed;
                    pitch = Mathf.Clamp(pitch - delta.y * rotateSpeed, 10f, 89f);
                }

                if (mouse.middleButton.isPressed && delta != Vector2.zero)
                {
                    Pan(-delta * distance * 0.0015f);
                }

                // 滑鼠在操作面板上時，滾輪是用來捲動清單的，不縮放畫面。
                var scroll = ProductionPanel.PointerOverPanel ? 0f : mouse.scroll.ReadValue().y;
                if (!Mathf.Approximately(scroll, 0f))
                {
                    distance = Mathf.Clamp(distance * (scroll > 0f ? 0.9f : 1.1f), minDistance, maxDistance);
                }
            }

            // 面板的輸入框有焦點時（例如打佈局名稱）鍵盤是用來打字的，不移動攝影機。
            var keyboard = ProductionPanel.TextInputFocused ? null : Keyboard.current;
            if (keyboard != null)
            {
                var move = Vector2.zero;
                if (keyboard.wKey.isPressed) move.y += 1f;
                if (keyboard.sKey.isPressed) move.y -= 1f;
                if (keyboard.dKey.isPressed) move.x += 1f;
                if (keyboard.aKey.isPressed) move.x -= 1f;
                if (move != Vector2.zero)
                {
                    Pan(move * keyPanSpeed * distance * Time.deltaTime);
                }

                if (keyboard.escapeKey.wasPressedThisFrame)
                {
                    Following = null;
                }

                if (keyboard.fKey.wasPressedThisFrame)
                {
                    Following = null;
                    _focusing = false;
                    target = _homeTarget;
                    yaw = _homeYaw;
                    pitch = _homePitch;
                    distance = _homeDistance;
                }
            }
        }

        private void LateUpdate()
        {
            if (Following != null)
            {
                target = Vector3.Lerp(target, Following.position, 1f - Mathf.Exp(-6f * Time.deltaTime));
            }
            else if (_focusing)
            {
                target = Vector3.Lerp(target, _focusPoint, 1f - Mathf.Exp(-8f * Time.deltaTime));
                if ((target - _focusPoint).sqrMagnitude < 0.0025f)
                {
                    target = _focusPoint;
                    _focusing = false;
                }
            }

            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(target - rotation * Vector3.forward * distance, rotation);
        }

        // 在地面上平移：x 沿畫面左右，y 沿畫面前後。
        private void Pan(Vector2 amount)
        {
            Following = null;
            _focusing = false;
            var flat = Quaternion.Euler(0f, yaw, 0f);
            target += flat * new Vector3(amount.x, 0f, amount.y);
        }
    }
}
