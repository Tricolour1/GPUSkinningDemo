using UnityEngine;

namespace GpuSkinningDemo
{
    public sealed class EditorPreviewOnly : MonoBehaviour
    {
        private void Awake() => gameObject.SetActive(false);
    }
}
