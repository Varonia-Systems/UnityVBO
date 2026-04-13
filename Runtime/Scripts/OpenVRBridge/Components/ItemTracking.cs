using System;
using UnityEngine;
using UnityEngine.Events;

#if STEAMVR_ENABLED
using Valve.VR;
#endif

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.OpenXR.Features.Interactions;
#endif

namespace VaroniaBackOffice
{
    public class ItemTracking : MonoBehaviour
    {
        // ─────────────────────────────────────────────
        //  ENUMS
        // ─────────────────────────────────────────────

        public enum TrackingBackend { SteamVR, OpenXR }
        public enum TrackingMode   { AutoByClass, ManualIndex }

        public enum OpenXRDeviceType
        {
            LeftController,
            RightController,
            ViveTracker
        }

        public enum ViveTrackerRole
        {
            HandheldObject,
            LeftFoot,
            RightFoot,
            LeftShoulder,
            RightShoulder,
            LeftElbow,
            RightElbow,
            LeftKnee,
            RightKnee,
            Waist,
            Chest,
            Camera,
            Keyboard
        }

        // ─────────────────────────────────────────────
        //  INSPECTOR
        // ─────────────────────────────────────────────

        [Header("Backend de Tracking")]
        public TrackingBackend backend = TrackingBackend.SteamVR;

        // ── SteamVR ──────────────────────────────────
        [Header("Configuration")]
        public TrackingMode mode = TrackingMode.AutoByClass;
        public bool autoFind = true;
        public bool applyToParent = false;
#if STEAMVR_ENABLED
        public ETrackedDeviceClass targetClass = ETrackedDeviceClass.GenericTracker;
#endif
        public int trackerIndex = 3;

        [Header("Filtre Avancé")]
        public bool useSerialFilter = false;
        public string targetSerial = "LHR-XXXXXXXX";

        // ── OpenXR ──────────────────────────────────
        [Header("OpenXR — Configuration")]
        public OpenXRDeviceType openXRDeviceType = OpenXRDeviceType.ViveTracker;
        public ViveTrackerRole  viveTrackerRole  = ViveTrackerRole.HandheldObject;

        // ── Commun ───────────────────────────────────
        [Header("Offsets")]
        public Vector3 positionOffset = Vector3.zero;
        public Vector3 rotationOffset = Vector3.zero;

        [Header("État (lecture seule)")]
        public bool   found      = false;
        public bool   isTracking = false;
        public int    foundIndex = -1;
        public string foundModel = "";
        public string foundSerial = "";

        [Header("Événements")]
        public UnityEvent OnTrackingLost;
        public UnityEvent OnTrackingRestored;

        // ─────────────────────────────────────────────
        //  PRIVÉ — SteamVR
        // ─────────────────────────────────────────────

#if STEAMVR_ENABLED
        private TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[64];
        private System.Text.StringBuilder _sb = new System.Text.StringBuilder(256);
        private CVRSystem vr;
#endif

        // ─────────────────────────────────────────────
        //  PRIVÉ — OpenXR
        // ─────────────────────────────────────────────

#if ENABLE_INPUT_SYSTEM
        private InputDevice _openXRDevice;
        private bool        _openXRBound = false;
#endif

        // ─────────────────────────────────────────────
        //  PRIVÉ — Commun
        // ─────────────────────────────────────────────

        private Vector3 _lastPosition    = Vector3.zero;
        private int     _frozenFrameCount = 0;

        // ─────────────────────────────────────────────
        //  ANTI-CRASH
        // ─────────────────────────────────────────────

        private static bool _dead = false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _dead = false;
        }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void EditorQuitHook()
        {
            UnityEditor.EditorApplication.playModeStateChanged += (state) =>
            {
                if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
                {
                    _dead = true;

                    // Désactive TOUS les ItemTracking immédiatement
                    foreach (var tracker in FindObjectsOfType<ItemTracking>())
                    {
#if STEAMVR_ENABLED
                        tracker.vr = null;
#endif
                        tracker.enabled = false;
                    }
                }
            };
        }
#endif

        // ─────────────────────────────────────────────
        //  UNITY LIFECYCLE
        // ─────────────────────────────────────────────

        void OnEnable()
        {
#if ENABLE_INPUT_SYSTEM
            if (backend == TrackingBackend.OpenXR)
                InputSystem.onDeviceChange += OnInputDeviceChange;
#endif
        }

        void OnDisable()
        {
#if ENABLE_INPUT_SYSTEM
            if (backend == TrackingBackend.OpenXR)
                InputSystem.onDeviceChange -= OnInputDeviceChange;
#endif

#if STEAMVR_ENABLED
            vr = null;
#endif
#if ENABLE_INPUT_SYSTEM
            _openXRBound  = false;
            _openXRDevice = null;
#endif
        }

        void OnApplicationQuit()
        {
            _dead = true;
#if STEAMVR_ENABLED
            vr = null;
#endif
        }

        void Update()
        {
            if (_dead) return;

            switch (backend)
            {
                case TrackingBackend.SteamVR: UpdateSteamVR(); break;
                case TrackingBackend.OpenXR:  UpdateOpenXR();  break;
            }
        }

        // ─────────────────────────────────────────────
        //  STEAMVR
        // ─────────────────────────────────────────────

        void UpdateSteamVR()
        {
#if STEAMVR_ENABLED
            if (vr == null)
                vr = SteamVRBridge.GetSystem();
            if (vr == null)
            {
                SetTrackingState(false);
                return;
            }
            if (_dead) return;

            vr.GetDeviceToAbsoluteTrackingPose(
                ETrackingUniverseOrigin.TrackingUniverseStanding, 0, _poses);

            if (autoFind)
                FindTracker();
            else
                foundIndex = trackerIndex;

            ApplyPose(foundIndex);
#else
            Debug.LogWarning("[ItemTracking] SteamVR backend sélectionné mais STEAMVR_ENABLED n'est pas défini.");
#endif
        }

#if STEAMVR_ENABLED
        void FindTracker()
        {
            if (vr == null || _dead) return;

            if (found && foundIndex != -1
                && (uint)foundIndex < 64
                && _poses[foundIndex].bPoseIsValid)
            {
                if (!useSerialFilter) return;
            }

            for (uint i = 0; i < 64; i++)
            {
                if (_dead) return;

                if (vr.GetTrackedDeviceClass(i) == targetClass && _poses[i].bPoseIsValid)
                {
                    string deviceSerial = GetPropertyString(i,
                        ETrackedDeviceProperty.Prop_SerialNumber_String);

                    if (useSerialFilter && !string.IsNullOrEmpty(targetSerial))
                    {
                        if (deviceSerial != targetSerial) continue;
                    }

                    found = true;
                    foundIndex = (int)i;
                    foundSerial = deviceSerial;
                    foundModel = GetPropertyString(i,
                        ETrackedDeviceProperty.Prop_ModelNumber_String);
                    return;
                }
            }
            found = false;
        }

        string GetPropertyString(uint index, ETrackedDeviceProperty prop)
        {
            if (vr == null || _dead) return "";

            ETrackedPropertyError err = ETrackedPropertyError.TrackedProp_Success;
            _sb.Clear();
            vr.GetStringTrackedDeviceProperty(index, prop, _sb, 256, ref err);
            return _sb.ToString();
        }

        void ApplyPose(int index)
        {
            if (index < 0 || index >= _poses.Length || !_poses[index].bPoseIsValid || _poses[index].eTrackingResult != ETrackingResult.Running_OK)
            {
                SetTrackingState(false);
                return;
            }

            Transform targetT = (applyToParent && transform.parent != null)
                ? transform.parent : transform;

            HmdMatrix34_t m = _poses[index].mDeviceToAbsoluteTracking;
            Vector3 rawPos = new Vector3(m.m3, m.m7, -m.m11);

            Matrix4x4 mat = Matrix4x4.identity;
            mat[0, 0] = m.m0;  mat[0, 1] = m.m1;  mat[0, 2] = -m.m2;
            mat[1, 0] = m.m4;  mat[1, 1] = m.m5;  mat[1, 2] = -m.m6;
            mat[2, 0] = -m.m8; mat[2, 1] = -m.m9; mat[2, 2] = m.m10;

            Quaternion rawRot = mat.rotation;
            targetT.localPosition = rawPos + (rawRot * positionOffset);
            targetT.localRotation = rawRot * Quaternion.Euler(rotationOffset);
           
            
            
            
            if (rawPos == _lastPosition)
            {
                _frozenFrameCount++;
                if (_frozenFrameCount >= 3) SetTrackingState(false);
            }
            else
            {
                _frozenFrameCount = 0;
                SetTrackingState(true);
            }
            _lastPosition = rawPos;
        }
#endif

        // ─────────────────────────────────────────────
        //  OPENXR
        // ─────────────────────────────────────────────

        void UpdateOpenXR()
        {
#if OPENXR
            if (!_openXRBound || _openXRDevice == null || !_openXRDevice.added)
                TryBindOpenXRDevice();

            if (_openXRDevice == null || !_openXRDevice.added)
            {
                SetTrackingState(false);
                return;
            }

            ApplyPoseOpenXR();
#else
            SetTrackingState(false);
#endif
        }

#if ENABLE_INPUT_SYSTEM
        void TryBindOpenXRDevice()
        {

            _openXRBound  = false;
            _openXRDevice = null;

            switch (openXRDeviceType)
            {
                case OpenXRDeviceType.LeftController:
                    _openXRDevice = FindXRController(XRController.leftHand);
                    break;

                case OpenXRDeviceType.RightController:
                    _openXRDevice = FindXRController(XRController.rightHand);
                    break;

                case OpenXRDeviceType.ViveTracker:
                    _openXRDevice = FindViveTracker(viveTrackerRole);
                    break;
            }

            _openXRBound = (_openXRDevice != null);
            found        = _openXRBound;
            foundIndex   = -1;
            foundSerial  = _openXRDevice?.description.serial ?? "";
            foundModel   = _openXRDevice?.description.product ?? "";

        }
#endif

#if ENABLE_INPUT_SYSTEM
        static InputDevice FindXRController(XRController hint)
        {

            return hint != null && hint.added ? hint : null;


        }
        
#endif
        
#if ENABLE_INPUT_SYSTEM
        static InputDevice FindViveTracker(ViveTrackerRole role)
        {

            string roleStr = RoleToString(role);
            foreach (var dev in InputSystem.devices)
            {
                if (dev is ViveTrackerProfile.ViveTracker)
                {
                    if (dev.displayName.IndexOf(roleStr, StringComparison.OrdinalIgnoreCase) >= 0)
                        return dev;
                }
            }

            return null;
        }
#endif

        static string RoleToString(ViveTrackerRole role)
        {
            switch (role)
            {
                case ViveTrackerRole.HandheldObject:  return "handheld_object";
                case ViveTrackerRole.LeftFoot:        return "left_foot";
                case ViveTrackerRole.RightFoot:       return "right_foot";
                case ViveTrackerRole.LeftShoulder:    return "left_shoulder";
                case ViveTrackerRole.RightShoulder:   return "right_shoulder";
                case ViveTrackerRole.LeftElbow:       return "left_elbow";
                case ViveTrackerRole.RightElbow:      return "right_elbow";
                case ViveTrackerRole.LeftKnee:        return "left_knee";
                case ViveTrackerRole.RightKnee:       return "right_knee";
                case ViveTrackerRole.Waist:           return "waist";
                case ViveTrackerRole.Chest:           return "chest";
                case ViveTrackerRole.Camera:          return "camera";
                case ViveTrackerRole.Keyboard:        return "keyboard";
                default:                              return "";
            }
        }

#if ENABLE_INPUT_SYSTEM
        void ApplyPoseOpenXR()
        {

            Vector3    pos;
            Quaternion rot;
            bool       tracked = false;

            if (openXRDeviceType == OpenXRDeviceType.LeftController ||
                openXRDeviceType == OpenXRDeviceType.RightController)
            {
                var ctrl = _openXRDevice as XRController;
                if (ctrl == null) { SetTrackingState(false); return; }

                pos     = ctrl.devicePosition.ReadValue();
                rot     = ctrl.deviceRotation.ReadValue();
                tracked = ctrl.isTracked.ReadValue() > 0.5f;
            }
            else
            {
                var tracker = _openXRDevice as ViveTrackerProfile.ViveTracker;
                if (tracker == null) { SetTrackingState(false); return; }

                pos     = tracker.devicePosition.ReadValue();
                rot     = tracker.deviceRotation.ReadValue();
                tracked = tracker.isTracked.ReadValue() > 0.5f;
            }

            if (!tracked)
            {
                SetTrackingState(false);
                return;
            }

            Transform targetT = (applyToParent && transform.parent != null)
                ? transform.parent : transform;

            targetT.localPosition = pos + (rot * positionOffset);
            targetT.localRotation = rot * Quaternion.Euler(rotationOffset);

            if (pos == _lastPosition)
            {
                _frozenFrameCount++;
                if (_frozenFrameCount >= 3) SetTrackingState(false);
            }
            else
            {
                _frozenFrameCount = 0;
                SetTrackingState(true);
            }
            _lastPosition = pos;

        }
#endif

#if ENABLE_INPUT_SYSTEM
        void OnInputDeviceChange(InputDevice dev, InputDeviceChange change)
        {

            if (change == InputDeviceChange.Added || change == InputDeviceChange.Removed)
                _openXRBound = false;
        }
#endif

        // ─────────────────────────────────────────────
        //  COMMUN
        // ─────────────────────────────────────────────

        public void Rescan()
        {
            found        = false;
            foundIndex   = -1;
            foundModel   = "";
            foundSerial  = "";
#if ENABLE_INPUT_SYSTEM
            _openXRBound  = false;
            _openXRDevice = null;
#endif
            SetTrackingState(false);
        }

        private void SetTrackingState(bool newState)
        {
            if (isTracking == newState) return;

            isTracking = newState;

            if (isTracking)
                OnTrackingRestored?.Invoke();
            else
                OnTrackingLost?.Invoke();
        }
    }
}
