using UnityEngine;

namespace OpenFleet.Unity
{
    /// <summary>讓文字標籤永遠面向主攝影機。</summary>
    public sealed class Billboard : MonoBehaviour
    {
        private Camera _camera;

        private void LateUpdate()
        {
            if (_camera == null)
            {
                _camera = Camera.main;
                if (_camera == null)
                {
                    return;
                }
            }

            transform.rotation = _camera.transform.rotation;
        }
    }
}
