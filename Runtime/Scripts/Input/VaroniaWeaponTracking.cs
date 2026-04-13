using System.Collections;
using UnityEngine;
using VaroniaBackOffice;

namespace VBO_Ultimate.Runtime.Scripts.Input
{
    public class VaroniaWeaponTracking : MonoBehaviour
    {
        [Header("Weapon Index")]
        [Tooltip("Index de cette arme dans VaroniaInput (0 = première arme, 1 = deuxième, etc.).")]
        public int weaponIndex = 0;

        [Header("Force ID")]
        public bool forceId = false;
        public int forcedId = 0;

        [Header("Tracker")]
        public ItemTracking trackerFollower;

        [Header("Override AutoFind")]
        [Tooltip("Si activé, override autoFind du ItemTracking avec les paramètres ci-dessous.")]
        public bool overrideAutoFind = false;
        public enum TrackingParentMode {ThisTransform, DirectItemTracking }
        public TrackingParentMode parentTrackingMode = TrackingParentMode.ThisTransform;

        // ── SteamVR Override ──
#if STEAMVR_ENABLED
        public bool overriddenAutoFind = false;
        public string overriddenTargetSerial = "LHR-XXXXXXXX";
        public bool overriddenUseSerialFilter = false;
        public int overriddenTrackerIndex = 3;
        public Valve.VR.ETrackedDeviceClass overriddenTargetClass = Valve.VR.ETrackedDeviceClass.GenericTracker;
#endif

        // ── Backend Override ──
        public ItemTracking.TrackingBackend overriddenBackend = ItemTracking.TrackingBackend.OpenXR;

        // ── OpenXR Override ──
        public ItemTracking.OpenXRDeviceType overriddenOpenXRDeviceType = ItemTracking.OpenXRDeviceType.ViveTracker;
        public ItemTracking.ViveTrackerRole  overriddenViveTrackerRole  = ItemTracking.ViveTrackerRole.HandheldObject;

        [Header("État du tracking (lecture seule)")]
        [SerializeField] private bool trackingLost = false;

        
        public _Weapon weap;
        


        private IEnumerator Start()
        {
            yield return new WaitUntil(() => VaroniaWeapon.Instance != null);

            int controllerId;

            if (forceId)
            {
                controllerId = forcedId;
                Debug.Log($"[VaroniaWeaponTracking] Force ID enabled — using ID: {controllerId}");
            }
            else
            {
                yield return new WaitUntil(() => BackOfficeVaronia.Instance != null && BackOfficeVaronia.Instance.config != null);
                controllerId = (int)BackOfficeVaronia.Instance.config.Controller;
            }

            _WeaponInfo entry = VaroniaWeapon.Instance.GetWeaponById(controllerId);

            if (entry != null && entry.prefabWeapon != null)
            {
                GameObject spawned = Instantiate(entry.prefabWeapon, transform.position, transform.rotation, transform);
                Debug.Log($"#[VaroniaWeaponTracking] Weapon spawned for Controller ID: {controllerId}");

                
                weap = spawned.GetComponent<_Weapon>();
                
                if (trackerFollower == null)
                    trackerFollower = spawned.GetComponentInChildren<ItemTracking>();

                ApplyTrackerSettings();
            }
            else
            {
                Debug.LogWarning($"[VaroniaWeaponTracking] No weapon prefab found for Controller ID: {controllerId}");
            }
        }

        private void ApplyTrackerSettings()
        {
            if (trackerFollower == null) return;

            // Toujours appliquer le mode de parenté
            trackerFollower.applyToParent = (parentTrackingMode == TrackingParentMode.ThisTransform);

            if (!overrideAutoFind) return;

            trackerFollower.backend = overriddenBackend;

#if STEAMVR_ENABLED
            if (trackerFollower.backend == ItemTracking.TrackingBackend.SteamVR)
            {
                trackerFollower.autoFind          = overriddenAutoFind;
                trackerFollower.targetSerial      = overriddenTargetSerial;
                trackerFollower.useSerialFilter   = overriddenUseSerialFilter;
                trackerFollower.trackerIndex      = overriddenTrackerIndex;
                trackerFollower.targetClass       = overriddenTargetClass;
            }
#endif

            if (trackerFollower.backend == ItemTracking.TrackingBackend.OpenXR)
            {
                trackerFollower.openXRDeviceType = overriddenOpenXRDeviceType;
                trackerFollower.viveTrackerRole  = overriddenViveTrackerRole;
                trackerFollower.Rescan();
            }
        }

        private void Update()
        {
            if (trackerFollower != null)
                trackingLost = !trackerFollower.isTracking;
        }
    }
}
