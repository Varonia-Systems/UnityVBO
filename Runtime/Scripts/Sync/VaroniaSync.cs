using UnityEngine;

namespace VaroniaBackOffice
{
    /// <summary>
    /// Applies SyncPos + SyncQuaternion on its transform and instantiates ONCE
    /// the boundaryPrefab, which manages all Boundaries itself.
    /// </summary>
    public class VaroniaSync : MonoBehaviour
    {
        // ─── Inspector ───────────────────────────────────────────────────────────

        [Tooltip("Prefab instantiated once. Must have a BoundaryVisual component.")]
        [SerializeField] private GameObject boundaryPrefab;

        [Tooltip("If true, instantiates the boundaryPrefab during Apply.")]
        [SerializeField] private bool instantiateBoundary = true;

        [Tooltip("If true, the instantiated GameObject will be disabled (SetActive false) at instantiation.")]
        [SerializeField] private bool startBoundaryInactive = false;

        // ─── Private ─────────────────────────────────────────────────────────────

        private GameObject _instance;

        // ─────────────────────────────────────────────────────────────────────────

        private void Start()
        {
            if (BackOfficeVaronia.Instance != null)
            {
                BackOfficeVaronia.Instance.Rig = transform;
            }

            if (VaroniaSpatialLoader.Data != null)
                Apply();
            else
                VaroniaSpatialLoader.OnLoaded += OnSpatialLoaded;
        }

        private void OnDestroy()
        {
            VaroniaSpatialLoader.OnLoaded -= OnSpatialLoaded;
        }

        private void OnSpatialLoaded()
        {
            VaroniaSpatialLoader.OnLoaded -= OnSpatialLoaded;
            Apply();
        }

        // ─── Apply ───────────────────────────────────────────────────────────────

        public void Apply()
        {
            var spatial = VaroniaSpatialLoader.Data as Spatial;
            if (spatial == null)
            {
                Debug.LogWarning("[VaroniaSync] Spatial data not found.");
                return;
            }

            // ── Transform ─────────────────────────────────────────────────────────
            if (spatial.SyncPos != null)
                transform.position = spatial.SyncPos.asVec3();

            if (spatial.SyncQuaterion != null)
                transform.rotation = spatial.SyncQuaterion.asQuat();

            // ── Prefab (single instance) ─────────────────────────────────────────
            if (_instance != null)
                Destroy(_instance);

            if (!instantiateBoundary)
                return;

            if (boundaryPrefab == null)
            {
                Debug.LogWarning("[VaroniaSync] No boundaryPrefab assigned.");
                return;
            }

            _instance = Instantiate(boundaryPrefab, transform);
            _instance.transform.localPosition = new Vector3(0,0.1f,0);
            if (startBoundaryInactive)
                _instance.SetActive(false);
            
        }

        // ─── Accessors (Editor) ──────────────────────────────────────────────────

        public bool HasPrefab              => boundaryPrefab != null;
        public bool InstantiateBoundary    => instantiateBoundary;
        public bool StartBoundaryInactive  => startBoundaryInactive;
    }
}
