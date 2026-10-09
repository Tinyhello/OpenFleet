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
        [SerializeField] private Vector3 target = new Vector3(0f, 0f, 1f);
        [SerializeField] private float yaw;
        [SerializeField] private float pitch = 50f;
        [SerializeField] private float distance = 42f;
        [SerializeField] private float minDistance = 5f;
        [SerializeField] private float maxDistance = 80f;
        [SerializeField] private float rotateSpeed = 0.25f;
        [SerializeField] private float keyPanSpeed = 0.6f;

        private Vector3 _homeTarget;
        private float _homeYaw;
        private float _homePitch;
        private float _homeDistance;

        public Transform Following { get; private set; }

        public void Follow(Transform vehicle)
        {
            Following = vehicle;
            if (vehicle != null)
            {
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

                var scroll = mouse.scroll.ReadValue().y;
                if (!Mathf.Approximately(scroll, 0f))
                {
                    distance = Mathf.Clamp(distance * (scroll > 0f ? 0.9f : 1.1f), minDistance, maxDistance);
                }
            }

            var keyboard = Keyboard.current;
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

            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(target - rotation * Vector3.forward * distance, rotation);
        }

        // 在地面上平移：x 沿畫面左右，y 沿畫面前後。
        private void Pan(Vector2 amount)
        {
            Following = null;
            var flat = Quaternion.Euler(0f, yaw, 0f);
            target += flat * new Vector3(amount.x, 0f, amount.y);
        }
    }
}
