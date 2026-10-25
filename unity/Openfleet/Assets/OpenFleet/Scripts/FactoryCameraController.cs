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
            _atHome = false;
            _focusPoint = new Vector3(point.x, 0f, point.z);
            _focusing = true;
        }

        public void Follow(Transform vehicle)
        {
            Following = vehicle;
            if (vehicle != null)
            {
                _atHome = false;
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

        // 預設視角要框住的廠房外框、當時被面板蓋住的寬度與視窗大小；變了就重算（見 FitHome）。
        private bool _hasFit;
        private float _fitMinX, _fitMaxX, _fitMinZ, _fitMaxZ;
        private float _fitCovered;
        private int _fitScreenWidth, _fitScreenHeight;

        // 使用者還沒動過攝影機（平移、旋轉、縮放、聚焦、跟隨都算動過）時，預設視角重算後攝影機跟著回去；動過就只更新 F 要回去的位置。
        private bool _atHome = true;

        // 預設視角的取景範圍（Claude 暫定）：只用剩下畫面的這個比例，四周留一點邊；牆高 1.2 m、柱子 4 m、門楣 4.65 m，取 4.5 m 算頂端。
        private const float FitMargin = 0.92f, FitTopHeight = 4.5f;
        private const float FitMinDistance = 20f, FitMaxDistance = 400f;

        /// <summary>
        /// 預設視角（使用者 2026-10-25 決定）：整個廠房（外牆外框）都在「沒被右邊操作面板蓋住的畫面」裡，
        /// 依攝影機的視野與面板寬度算出目標與距離，所以可能比以前固定的 56 m 更遠。
        /// 面板收起來、換介面大小或視窗大小時自動重算（見 <see cref="Update"/>）；
        /// <paramref name="moveCamera"/> 為 true 時攝影機也回到新的預設視角，否則只在使用者還沒動過攝影機時跟著移。
        /// </summary>
        public void FitHome(float minX, float maxX, float minZ, float maxZ, bool moveCamera)
        {
            _hasFit = true;
            _fitMinX = minX;
            _fitMaxX = maxX;
            _fitMinZ = minZ;
            _fitMaxZ = maxZ;
            RefreshHome(moveCamera || _atHome);
        }

        private void RefreshHome(bool moveCamera)
        {
            _fitCovered = ProductionPanel.CoveredPixels;
            _fitScreenWidth = Screen.width;
            _fitScreenHeight = Screen.height;
            var camera = GetComponent<Camera>();
            var fov = camera != null ? camera.fieldOfView : 45f;
            var (homeTarget, homeDistance) = ComputeFit(fov, (float)Screen.width / Mathf.Max(1, Screen.height), _fitCovered / Mathf.Max(1, Screen.width));
            SetHome(homeTarget, homeDistance, moveCamera);
            if (camera != null && camera.farClipPlane < maxDistance + 200f)
            {
                // 場景裡的遠裁切面是 300 m；拉得很遠時（大廠房）要跟著加大，不然遠端的牆會被切掉。
                camera.farClipPlane = maxDistance + 200f;
            }
        }

        /// <summary>
        /// 在預設的角度（俯角、方位）下，找最近的距離與對應的目標點，使外框（地面與頂端兩層的角點）投影後
        /// 落在視野扣掉右側 <paramref name="coveredFraction"/> 寬度之後的範圍內，並置中於那個範圍。
        /// 目標點隨距離改變（透視下置中要的位移和距離有關），所以對距離二分搜尋，每個距離再疊代求目標點。
        /// </summary>
        private (Vector3 Target, float Distance) ComputeFit(float fovDegrees, float aspect, float coveredFraction)
        {
            var rotation = Quaternion.Euler(_homePitch, _homeYaw, 0f);
            var forward = rotation * Vector3.forward;
            var right = rotation * Vector3.right;
            var up = rotation * Vector3.up;
            var groundForward = new Vector3(forward.x, 0f, forward.z).normalized;
            var sinPitch = Mathf.Max(0.2f, Mathf.Sin(_homePitch * Mathf.Deg2Rad));
            var tanHalf = Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);
            var covered = Mathf.Clamp(coveredFraction, 0f, 0.7f);
            // 可見範圍的中心在 NDC 的 (-covered, 0)，半寬 (1 - covered) × 邊界比例，半高 1 × 邊界比例。
            var halfWidth = (1f - covered) * FitMargin;
            var halfHeight = FitMargin;

            var corners = new Vector3[8];
            var index = 0;
            foreach (var y in new[] { 0f, FitTopHeight })
            {
                corners[index++] = new Vector3(_fitMinX, y, _fitMinZ);
                corners[index++] = new Vector3(_fitMaxX, y, _fitMinZ);
                corners[index++] = new Vector3(_fitMinX, y, _fitMaxZ);
                corners[index++] = new Vector3(_fitMaxX, y, _fitMaxZ);
            }

            var center = new Vector3((_fitMinX + _fitMaxX) / 2f, 0f, (_fitMinZ + _fitMaxZ) / 2f);

            bool Fits(float candidate, out Vector3 candidateTarget)
            {
                candidateTarget = center;
                var fits = false;
                for (var pass = 0; pass < 10; pass++)
                {
                    var cameraPosition = candidateTarget - forward * candidate;
                    float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                    foreach (var corner in corners)
                    {
                        var v = corner - cameraPosition;
                        var depth = Mathf.Max(0.1f, Vector3.Dot(v, forward));
                        var x = Vector3.Dot(v, right) / (depth * tanHalf * aspect);
                        var y = Vector3.Dot(v, up) / (depth * tanHalf);
                        minX = Mathf.Min(minX, x);
                        maxX = Mathf.Max(maxX, x);
                        minY = Mathf.Min(minY, y);
                        maxY = Mathf.Max(maxY, y);
                    }

                    fits = (maxX - minX) / 2f <= halfWidth && (maxY - minY) / 2f <= halfHeight;
                    var errorX = (minX + maxX) / 2f + covered;
                    var errorY = (minY + maxY) / 2f;
                    candidateTarget += right * (errorX * candidate * tanHalf * aspect) + groundForward * (errorY * candidate * tanHalf / sinPitch);
                    candidateTarget.y = 0f;
                }

                return fits;
            }

            float low = FitMinDistance, high = FitMaxDistance;
            Vector3 target;
            if (Fits(low, out target))
            {
                return (target, low);
            }

            for (var i = 0; i < 24; i++)
            {
                var mid = (low + high) / 2f;
                if (Fits(mid, out _))
                {
                    high = mid;
                }
                else
                {
                    low = mid;
                }
            }

            Fits(high, out target);
            return (target, high);
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
            _atHome = true;
            target = _homeTarget;
            yaw = _homeYaw;
            pitch = _homePitch;
            distance = _homeDistance;
        }

        private void Update()
        {
            // 面板收起／展開、介面大小或視窗大小變了：預設視角重算（使用者沒動過攝影機的話攝影機也跟著回去）。
            if (_hasFit && (!Mathf.Approximately(_fitCovered, ProductionPanel.CoveredPixels) || _fitScreenWidth != Screen.width || _fitScreenHeight != Screen.height))
            {
                RefreshHome(_atHome);
            }

            var mouse = Mouse.current;
            if (mouse != null)
            {
                var delta = mouse.delta.ReadValue();
                if (mouse.rightButton.isPressed)
                {
                    _atHome = false;
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
                    _atHome = false;
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
                    _atHome = true;
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
            _atHome = false;
            var flat = Quaternion.Euler(0f, yaw, 0f);
            target += flat * new Vector3(amount.x, 0f, amount.y);
        }
    }
}
