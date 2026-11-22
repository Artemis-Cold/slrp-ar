using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Aligns the building map using stable AR tracked-image samples. Every image
/// whose name matches a child of CalibrationPoints can establish the map pose.
/// Each marker Transform represents the floor below the QR; its forward (+Z)
/// points out from the wall toward the hallway/camera.
/// </summary>
public class QRMapCalibrator : MonoBehaviour
{
    [Header("AR References")]
    [SerializeField] private ARTrackedImageManager trackedImageManager;
    [SerializeField] private Transform arMapOrigin;

    [Header("Calibration markers")]
    [Tooltip("Parent containing Room3_Calibration, Room1_Calibration, etc. Auto-finds CalibrationPoints if left empty.")]
    [SerializeField] private Transform calibrationPointsRoot;
    [Tooltip("Legacy Room3 fallback; keep current Inspector reference unchanged.")]
    [SerializeField] private Transform room3Calibration;

    [System.Serializable]
    private struct MarkerHeightOverride
    {
        public string imageName;
        [Min(0f)] public float qrCenterHeightMeters;
    }

    [Tooltip("Optional per-marker real-world mounting heights; names must match Reference Image Library exactly.")]
    [SerializeField] private MarkerHeightOverride[] markerHeightOverrides =
        new MarkerHeightOverride[0];
    public string LastCalibratedMarker { get; private set; } = string.Empty;
    private string candidateMarkerName = string.Empty;

    [Header("Parish QR identity safety")]
    [Tooltip("For F1_/F2_ QR markers, require the camera-decoded QR payload to match the AR-tracked image name. Prevents visually similar parish QR codes from calibrating as the wrong location.")]
    [SerializeField] private bool verifyParishQrContents = true;
    private WayfinderTrackingWarningUI qrContentsDecoder;
    private float nextIdentityLogAt = -100f;

    [Header("Tracked Image Orientation")]
    [Tooltip("Optional 180-degree override ONLY after validating physical orientation of all markers.")]
    [SerializeField] private bool flipImageNormal = false;
    [Tooltip("Optional measured correction in degrees if the real hallway heading disagrees with the virtual map. Start at zero; do not guess 90 degrees.")]
    [SerializeField, Range(-30f, 30f)] private float mapYawCorrectionDegrees = 0f;

    [Header("Physical QR mounting height")]
    [Tooltip("Actual distance from floor to QR center. All marker transforms are at virtual floor height; per-marker overrides are below.")]
    [SerializeField, Min(0f)] private float qrCenterHeightAboveFloor = 1.20f;

    [Header("Calibration Stability")]
    [SerializeField, Min(0.5f)] private float stableTrackingSeconds = 1.25f;
    [SerializeField, Range(5, 100)] private int minimumPoseSamples = 12;
    [SerializeField, Min(0.03f)] private float maximumPositionDeviationMeters = 0.12f;
    [SerializeField, Range(2f, 35f)] private float maximumYawDeviationDegrees = 10f;
    [SerializeField, Range(0.1f, 1f)] private float lostTrackingResetSeconds = 0.65f;

    private struct PoseSample
    {
        public float time;
        public Vector3 position;
        public float yaw;
    }

    private readonly List<PoseSample> recentSamples = new List<PoseSample>();
    private float lastTrackedAt = -100f;
    private bool calibrated;

    public bool IsCalibrated => calibrated;
    // Explain precisely why scanning is still on Step 01 without bypassing
    // QR identity, tracking-state, or stable-pose requirements.
    public string CalibrationHint { get; private set; } = "Scan a location QR code";
    private float nextCalibrationStatusAt = -100f;
    private float lastMatchingImageAt = -100f;

    private void ReportCalibrationStage(string hint, string detail)
    {
        CalibrationHint = hint;
        if (Time.time < nextCalibrationStatusAt) return;
        Debug.Log("[SLRP AR QR] " + detail);
        nextCalibrationStatusAt = Time.time + 4f;
    }
    public bool IsCollectingSamples => !calibrated && recentSamples.Count > 0;
    public float CalibrationProgress
    {
        get
        {
            if (calibrated) return 1f;
            if (recentSamples.Count == 0) return 0f;
            float duration = Time.time - recentSamples[0].time;
            float timeProgress = duration / Mathf.Max(0.5f, stableTrackingSeconds);
            float countProgress = (float)recentSamples.Count / Mathf.Max(1, minimumPoseSamples);
            return Mathf.Clamp01(Mathf.Min(timeProgress, countProgress));
        }
    }

    private void Awake()
    {
        if (qrContentsDecoder == null)
            qrContentsDecoder = FindAnyObjectByType<WayfinderTrackingWarningUI>();
        if (calibrationPointsRoot == null)
        {
            GameObject root = GameObject.Find("CalibrationPoints");
            if (root != null) calibrationPointsRoot = root.transform;
        }
    }

    private Transform FindMatchingMarker(string imageName)
    {
        // Retired exterior checkpoints must never calibrate from a stale scene/library.
        if (imageName == "F1_BellTower_QR" || imageName == "F1_ComfortRoom_QR") return null;
        if (calibrationPointsRoot != null)
        {
            Transform marker = calibrationPointsRoot.Find(imageName);
            if (marker != null) return marker;
        }
        // Support the original Room3-only scene without requiring new wiring.
        if (room3Calibration != null && room3Calibration.name == imageName)
            return room3Calibration;
        return null;
    }

    private float GetQrCenterHeight(string markerName)
    {
        if (markerHeightOverrides != null)
        {
            foreach (MarkerHeightOverride item in markerHeightOverrides)
                if (item.imageName == markerName)
                    return Mathf.Max(0f, item.qrCenterHeightMeters);
        }
        return Mathf.Max(0f, qrCenterHeightAboveFloor);
    }

    private void Start()
    {
#if UNITY_IOS && !UNITY_EDITOR
        // ARKit may detect a stationary image only once with the default 0
        // moving-image budget. Stable calibration needs continuing pose updates.
        // This is deliberately iOS-only: do not change working ARCore behavior.
        if (trackedImageManager != null)
        {
            try
            {
                int previous = trackedImageManager.requestedMaxNumberOfMovingImages;
                if (previous < 1)
                    trackedImageManager.requestedMaxNumberOfMovingImages = 1;
                Debug.Log("[SLRP AR QR][iOS] ARKit moving-image tracking requested: " +
                    trackedImageManager.requestedMaxNumberOfMovingImages +
                    ". Image tracking must still report Tracking, not Limited.");
            }
            catch (System.Exception error)
            {
                Debug.LogWarning("[SLRP AR QR][iOS] Could not request moving-image tracking: " +
                    error.Message + ". Set Max Number of Moving Images = 1 in the Inspector.");
            }
        }
        else
        {
            Debug.LogError("[SLRP AR QR][iOS] QRMapCalibrator has no ARTrackedImageManager assigned.");
        }
#endif
    }

    private void OnEnable()
    {
        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.AddListener(OnTrackedImagesChanged);
    }

    private void OnDisable()
    {
        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.RemoveListener(OnTrackedImagesChanged);
    }

    private void Update()
    {
        if (calibrated) return;

        // Some ARCore / ARKit providers deliver only intermittent "changed"
        // callbacks for a stationary image. While the subsystem explicitly
        // reports Tracking, also sample its currently tracked objects. This
        // avoids a Step 01 deadlock without accepting Limited/None poses.
        // TryCalibrate rate-limits samples, including duplicate callbacks.
        if (trackedImageManager != null && trackedImageManager.enabled)
            foreach (ARTrackedImage image in trackedImageManager.trackables)
                TryCalibrate(image);

        // Do not combine pose samples across a real tracking interruption.
        if (recentSamples.Count > 0 &&
            Time.time - lastTrackedAt > lostTrackingResetSeconds)
        {
            ClearCandidate();
            ReportCalibrationStage("Hold QR steady",
                "Tracking updates stopped before the pose was stable; resetting samples.");
        }

        // Decoding QR text succeeds independently of AR Foundation image
        // tracking. Do not claim a complete scan if the second stage is absent.
        if (qrContentsDecoder != null && Time.time - lastMatchingImageAt > 2f)
        {
            string decodedMarker;
            if (qrContentsDecoder.TryGetRecentlyDecodedParishMarker(out decodedMarker))
                ReportCalibrationStage("QR verified — locating...",
                    "Payload verified for " + decodedMarker +
                    ", but no matching image pose is available yet. " +
                    "Keep the entire QR visible and well lit.");
        }
    }

    private void OnTrackedImagesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args)
    {
        if (calibrated) return;
        foreach (ARTrackedImage image in args.added) TryCalibrate(image);
        foreach (ARTrackedImage image in args.updated) TryCalibrate(image);
    }

    private void TryCalibrate(ARTrackedImage image)
    {
        if (calibrated || image == null) return;
        string markerName = image.referenceImage.name;
        Transform mapMarker = FindMatchingMarker(markerName);
        if (mapMarker == null) return; // Unknown image cannot align the map.

#if UNITY_IOS && !UNITY_EDITOR
        // An image can be detected without having a pose that is safe to use.
        // Log this distinction so the Xcode console identifies the exact stage.
        if (image.trackingState != TrackingState.Tracking && Time.time >= nextIdentityLogAt)
        {
            Debug.LogWarning("[SLRP AR QR][iOS] IMAGE SEEN: " + markerName +
                ", tracking state = " + image.trackingState +
                ". Calibration requires Tracking. Keep the whole marker visible.");
            nextIdentityLogAt = Time.time + 4f;
        }
#endif

        // ARCore/ARKit image matching looks at visual patterns, NOT the QR
        // payload. Similar QR patterns can appear as TWO names at the same
        // physical pose. Only the payload establishes which parish marker it is.
        if (verifyParishQrContents &&
            (markerName.StartsWith("F1_", System.StringComparison.Ordinal) ||
             markerName.StartsWith("F2_", System.StringComparison.Ordinal)))
        {
            if (qrContentsDecoder == null)
                qrContentsDecoder = FindAnyObjectByType<WayfinderTrackingWarningUI>();
            string decodedMarker;
            if (qrContentsDecoder == null ||
                !qrContentsDecoder.TryGetRecentlyDecodedParishMarker(out decodedMarker))
            {
                ReportCalibrationStage("Checking QR identity",
                    "AR image " + markerName +
                    " detected; waiting for its QR content to be decoded.");
                return;
            }
            if (!string.Equals(markerName, decodedMarker, System.StringComparison.Ordinal))
            {
                ReportCalibrationStage("QR mismatch — rescan",
                    "IMAGE NAME CONFLICT: AR tracking says " + markerName +
                    " but decoded QR says " + decodedMarker +
                    ". Calibration safely blocked.");
                return;
            }
        }

        if (trackedImageManager == null || arMapOrigin == null)
        {
            Debug.LogError("QRMapCalibrator: Assign Tracked Image Manager and ARMapOrigin.");
            return;
        }
        lastMatchingImageAt = Time.time;
        if (image.trackingState != TrackingState.Tracking)
        {
            if (candidateMarkerName == markerName) ClearCandidate();
            ReportCalibrationStage("QR verified — tracking...",
                "Matching image " + markerName + " is " + image.trackingState +
                "; calibration needs Tracking. Check lighting, distance and QR size.");
            return;
        }

        // Samples from DIFFERENT markers must never be averaged together.
        if (candidateMarkerName != markerName)
        {
            recentSamples.Clear();
            lastTrackedAt = -100f;
            candidateMarkerName = markerName;
        }

        Camera camera = Camera.main;
        if (camera == null) return;
        Vector3 virtualForward = Vector3.ProjectOnPlane(mapMarker.forward, Vector3.up);
        Vector3 physicalNormal = Vector3.ProjectOnPlane(image.transform.up, Vector3.up);
        Vector3 towardCamera = Vector3.ProjectOnPlane(
            camera.transform.position - image.transform.position, Vector3.up);

        // The virtual marker's forward faces the accessible hallway. When the
        // real image is scanned from its front, that front faces the camera.
        if (virtualForward.sqrMagnitude < 0.04f ||
            physicalNormal.sqrMagnitude < 0.04f || towardCamera.sqrMagnitude < 0.0025f)
        {
            ReportCalibrationStage("Adjust scanning angle",
                "No reliable horizontal heading from marker/camera pose " + markerName +
                ". Image up projection=" + physicalNormal.magnitude.ToString("F2") +
                "; camera separation=" + towardCamera.magnitude.ToString("F2"));
            return;
        }
        physicalNormal.Normalize();
        towardCamera.Normalize();
        if (Vector3.Dot(physicalNormal, towardCamera) < 0f)
            physicalNormal = -physicalNormal;
        // An almost edge-on image yields an unreliable heading.
        if (Vector3.Dot(physicalNormal, towardCamera) < 0.25f)
        {
            ReportCalibrationStage("Face the QR directly",
                "Image is too edge-on to establish heading for " + markerName + ".");
            return;
        }
        if (flipImageNormal)
            physicalNormal = -physicalNormal;

        float now = Time.time;
        if (now - lastTrackedAt > lostTrackingResetSeconds)
            recentSamples.Clear();
        lastTrackedAt = now;
        if (recentSamples.Count > 0 &&
            now - recentSamples[recentSamples.Count - 1].time < 0.035f)
            return;

        float yaw = Vector3.SignedAngle(virtualForward, physicalNormal, Vector3.up);
        recentSamples.Add(new PoseSample
        {
            time = now,
            position = image.transform.position,
            yaw = yaw
        });

        // A rolling window lets calibration settle after the initial image lock.
        float windowStart = now - Mathf.Max(2.5f, stableTrackingSeconds + 0.45f);
        while (recentSamples.Count > 0 && recentSamples[0].time < windowStart)
            recentSamples.RemoveAt(0);

        if (recentSamples.Count < minimumPoseSamples ||
            now - recentSamples[0].time < stableTrackingSeconds)
        {
            CalibrationHint = "QR verified — stabilizing " +
                Mathf.RoundToInt(CalibrationProgress * 100f) + "%";
            if (Time.time >= nextCalibrationStatusAt)
                ReportCalibrationStage(CalibrationHint,
                    "Stable-pose samples " + recentSamples.Count + "/" +
                    minimumPoseSamples + ", span " +
                    (now - recentSamples[0].time).ToString("F2") + "/" +
                    stableTrackingSeconds.ToString("F2") + " seconds for " + markerName);
            return;
        }

        Vector3 averagePosition = Vector3.zero;
        float sine = 0f;
        float cosine = 0f;
        foreach (PoseSample sample in recentSamples)
        {
            averagePosition += sample.position;
            sine += Mathf.Sin(sample.yaw * Mathf.Deg2Rad);
            cosine += Mathf.Cos(sample.yaw * Mathf.Deg2Rad);
        }
        averagePosition /= recentSamples.Count;
        float averageYaw = Mathf.Atan2(sine, cosine) * Mathf.Rad2Deg;

        float worstYaw = 0f;
        float worstPosition = 0f;
        foreach (PoseSample sample in recentSamples)
        {
            worstYaw = Mathf.Max(worstYaw,
                Mathf.Abs(Mathf.DeltaAngle(sample.yaw, averageYaw)));
            worstPosition = Mathf.Max(worstPosition,
                Vector3.Distance(sample.position, averagePosition));
        }

        if (worstYaw > maximumYawDeviationDegrees ||
            worstPosition > maximumPositionDeviationMeters)
        {
            ReportCalibrationStage("Hold the QR still",
                "Pose still unstable: yaw deviation=" + worstYaw.ToString("F1") +
                " deg (max " + maximumYawDeviationDegrees.ToString("F1") +
                "), position deviation=" + worstPosition.ToString("F3") +
                " m (max " + maximumPositionDeviationMeters.ToString("F3") + ").");
            return; // Do not accept a noisy calibration.
        }

        // Keep yaw-only rotation to prevent arrows tilting with the tracked image.
        // Correction is an Inspector calibration parameter, not an assumed turn angle.
        Quaternion mapRotation = Quaternion.Euler(0f,
            averageYaw + mapYawCorrectionDegrees, 0f);
        Vector3 floorBelowQR = averagePosition -
                               Vector3.up * GetQrCenterHeight(markerName);
        arMapOrigin.SetPositionAndRotation(
            floorBelowQR - mapRotation * mapMarker.position,
            mapRotation);
        calibrated = true;
        CalibrationHint = "QR calibrated";
        LastCalibratedMarker = markerName;
        ClearCandidate();

        Debug.Log("=== QR CALIBRATION COMPLETE (STABLE) ===");
        Debug.Log("Marker " + markerName + "; samples/heading/position noise: " +
            minimumPoseSamples + " / " + worstYaw.ToString("F1") + " deg / " +
            worstPosition.ToString("F3") + " m");
        Debug.Log("ARMapOrigin Position: " + arMapOrigin.position);
        Debug.Log("ARMapOrigin Rotation: " + arMapOrigin.eulerAngles);
        Debug.Log("QR yaw measured: " + averageYaw.ToString("F1") +
                  " degrees; correction: " + mapYawCorrectionDegrees.ToString("F1") + " degrees");
    }

    private void ClearCandidate()
    {
        recentSamples.Clear();
        lastTrackedAt = -100f;
        candidateMarkerName = string.Empty;
    }

    public void ResetCalibration()
    {
        calibrated = false;
        CalibrationHint = "Scan a location QR code";
        lastMatchingImageAt = -100f;
        nextCalibrationStatusAt = -100f;
        LastCalibratedMarker = string.Empty;
        ClearCandidate();
        if (arMapOrigin != null)
            arMapOrigin.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
    }
}
