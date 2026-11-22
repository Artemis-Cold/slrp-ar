using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Walkable NavMesh navigation in fixed house/map coordinates.
/// Displays a REUSED TRAIL of arrows along the route, not a camera-following arrow.
/// ARMapOrigin does the one-time QR map alignment. No manual arrow placement.
/// </summary>
public class IndoorPathVisualizer : MonoBehaviour
{
    [Header("Navigation")]
    [SerializeField] private Transform arCamera;
    [SerializeField] private Transform destinationPoint;

    [Header("Coordinate Mapping")]
    [SerializeField] private Transform arMapOrigin;

    [Header("QR Calibration")]
    [SerializeField] private QRMapCalibrator qrMapCalibrator;

    [Header("Crimson Route Trail")]
    [SerializeField] private Transform pathRendererParent;
    [SerializeField] private GameObject arrowPrefab;
    [SerializeField, Min(0.2f)] private float arrowSpacing = 0.7f;
    [SerializeField] private float arrowHeight = 0.05f;
    [SerializeField, Min(0.1f)] private float arrowScale = 1f;
    [SerializeField] private bool showRouteTrail = true;
    [SerializeField, Min(1)] private int maximumVisibleArrows = 45;
    [SerializeField, Min(0f)] private float firstArrowDistance = 0.55f;
    [SerializeField, Min(0f)] private float arrowEndClearance = 0.55f;

    [Header("Legacy settings, retained for Inspector compatibility")]
    [SerializeField] private bool fixedRouteArrows = false;
    [SerializeField] private Transform firstRouteStartMarker;
    [SerializeField] private bool startNextRouteFromLastArrival = false;

    [Header("Navigation / Arrival")]
    [SerializeField, Min(0.1f)] private float recalculateInterval = 0.35f;
    [SerializeField, Min(0.2f)] private float navMeshSearchRadius = 2f;
    [SerializeField, Min(0.2f)] private float arrivalDistance = 0.8f;

    private NavMeshPath path;
    private float nextRouteUpdate;
    private bool hasArrived;
    private readonly List<GameObject> pooledArrows = new List<GameObject>();
    private float nextArrowDiagnosticAt = -100f;

    [Header("Tracking safety (device)")]
    [Tooltip("After AR session tracking resumes, require this much continuous tracking before routing again.")]
    [SerializeField, Min(0.3f)] private float trackingStableBeforeRouteSeconds = 1.5f;
    [Tooltip("Camera position jumps this far between nearby frames indicate an unsafe AR pose; require QR recalibration.")]
    [SerializeField, Min(0.5f)] private float suspiciousPositionJumpMeters = 1.2f;
    [Tooltip("A long tracking outage requires recalibration; Unity cannot verify accumulated map drift.")]
    [SerializeField, Min(2f)] private float requireRescanAfterTrackingLossSeconds = 5f;
    [Tooltip("Useful only for XR Simulation testing. Editor does not behave like real AR tracking.")]
    [SerializeField] private bool enforceTrackingSafetyInEditor = false;

    private bool routeSafetyPaused;
    private bool requireQrRescan;
    private bool trackingAlertNeeded;
    private bool everStableTracking;
    private float stableTrackingSince = -1f;
    private float trackingLostSince = -1f;
    private float previousCameraSampleTime = -1f;
    private Vector3 previousCameraWorldPosition;
    private bool hasPreviousCameraSample;
    private string safetyExplanation = "Waiting for AR tracking to stabilize.";

    public bool IsRouteSafetyPaused => routeSafetyPaused || requireQrRescan;
    public bool RequiresQrRescan => requireQrRescan;
    public bool TrackingSafetyAlertNeeded => trackingAlertNeeded || requireQrRescan;
    public string TrackingSafetyMessage => safetyExplanation;

    // Reset when scanning a new marker. The prior AR coordinate frame is no
    // longer trustworthy after ARSession.Reset().
    public void ResetSafetyForRescan()
    {
        routeSafetyPaused = false;
        requireQrRescan = false;
        trackingAlertNeeded = false;
        everStableTracking = false;
        stableTrackingSince = -1f;
        trackingLostSince = -1f;
        previousCameraSampleTime = -1f;
        hasPreviousCameraSample = false;
        safetyExplanation = "Waiting for AR tracking to stabilize.";
        ClearArrows();
    }

    private void UpdateTrackingSafety()
    {
        if (qrMapCalibrator == null || !qrMapCalibrator.IsCalibrated)
        {
            // Only reset when a QR calibration has actually been discarded.
            // Avoid recreating a stale route or carrying the previous pose into
            // a new AR session.
            if (routeSafetyPaused || requireQrRescan || hasPreviousCameraSample ||
                everStableTracking) ResetSafetyForRescan();
            return;
        }

        if (requireQrRescan)
        {
            routeSafetyPaused = true;
            ClearArrows();
            return;
        }

        // Editor Play Mode and XR Simulation are not a reliable AR loss test.
        // On Android/iPhone the AR session MUST report SessionTracking.
        bool tracking = (Application.isEditor && !enforceTrackingSafetyInEditor)
            || UnityEngine.XR.ARFoundation.ARSession.state ==
               UnityEngine.XR.ARFoundation.ARSessionState.SessionTracking;
        float now = Time.unscaledTime;
        if (!tracking)
        {
            if (trackingLostSince < 0f) trackingLostSince = now;
            stableTrackingSince = -1f;
            routeSafetyPaused = true;
            if (everStableTracking || now - trackingLostSince > 2f)
                trackingAlertNeeded = true;
            safetyExplanation = "AR tracking interrupted. Move to better lighting and keep the phone steady.";
            ClearArrows();
            return;
        }

        if (trackingLostSince >= 0f)
        {
            // A prolonged outage means the device may have moved farther than
            // its tracking can reliably reconcile. Do not auto-reroute then.
            if (now - trackingLostSince >= requireRescanAfterTrackingLossSeconds)
            {
                RequireRecalibration("AR tracking was unavailable for several seconds. Rescan a QR near the stairs.");
                return;
            }
            if (hasPreviousCameraSample &&
                Vector3.Distance(previousCameraWorldPosition, arCamera.position) >
                suspiciousPositionJumpMeters)
            {
                RequireRecalibration("The phone's estimated position jumped after tracking loss. Rescan a QR near the stairs.");
                return;
            }
            trackingLostSince = -1f;
            stableTrackingSince = now;
        }

        // Only accept a fresh path after stable tracking for a short interval.
        if (stableTrackingSince < 0f) stableTrackingSince = now;
        if (now - stableTrackingSince < trackingStableBeforeRouteSeconds)
        {
            routeSafetyPaused = true;
            safetyExplanation = "Waiting for AR tracking to stabilize before updating the route.";
            ClearArrows();
            return;
        }

        if (hasPreviousCameraSample && previousCameraSampleTime >= 0f)
        {
            float elapsed = now - previousCameraSampleTime;
            float distance = Vector3.Distance(previousCameraWorldPosition, arCamera.position);
            if (elapsed > 0.001f && elapsed <= 0.5f &&
                distance > Mathf.Max(suspiciousPositionJumpMeters, 3.5f * elapsed))
            {
                RequireRecalibration("The estimated AR position jumped unexpectedly. Rescan a QR near the stairs.");
                return;
            }
        }

        // Recover after a short, stable outage; the UI still advises checking
        // arrows because smooth tracking drift cannot be automatically detected.
        routeSafetyPaused = false;
        trackingAlertNeeded = false;
        everStableTracking = true;
        safetyExplanation = string.Empty;
        previousCameraWorldPosition = arCamera.position;
        previousCameraSampleTime = now;
        hasPreviousCameraSample = true;
    }

    private void RequireRecalibration(string explanation)
    {
        routeSafetyPaused = true;
        requireQrRescan = true;
        trackingAlertNeeded = true;
        safetyExplanation = explanation;
        ClearArrows();
        Debug.LogWarning("SLRP AR navigation safety: " + explanation);
    }

    // Optional scene-authored standing point for a mounted QR. The actual QR
    // image transform can remain on a wall while route preview and audits
    // start from a reachable public walkway. The live AR navigator instead
    // starts from the device camera pose after QR calibration.
    public static Vector3 GetNavStartPosition(Transform checkpoint)
    {
        if (checkpoint == null) return Vector3.zero;
        Transform standing = checkpoint.Find("NavigationStart");
        return standing != null && standing.gameObject.activeInHierarchy
            ? standing.position : checkpoint.position;
    }

    // Optional scene-authored entrance point. Keeps the official destination
    // transform (and all saved calibration data) unchanged. Add a child named
    // "NavigationArrival" under a destination and place it on the accessible
    // walkway immediately OUTSIDE the entrance. Stair waypoints work normally.
    public static Vector3 GetNavTargetPosition(Transform destination)
    {
        if (destination == null) return Vector3.zero;
        Transform arrival = destination.Find("NavigationArrival");
        return arrival != null && arrival.gameObject.activeInHierarchy
            ? arrival.position : destination.position;
    }

    public event Action<Transform> DestinationReached;
    public Transform CurrentDestination => destinationPoint;
    public bool HasArrived => hasArrived;
    // Exposes failed route checks to the UI; a failed calculation is NOT an endless loading state.
    public bool HasValidRoute { get; private set; }
    public string LastRouteIssue { get; private set; } = string.Empty;
    public float EstimatedRemainingMapMeters { get; private set; } = -1f;

    private void ReportRouteIssue(string problem)
    {
        if (LastRouteIssue != problem)
            Debug.LogWarning("[SLRP AR route] " + (destinationPoint != null ? destinationPoint.name : "none") + ": " + problem);
        LastRouteIssue = problem;
        HasValidRoute = false;
        EstimatedRemainingMapMeters = -1f;
        ClearArrows();
    }

    private void Start()
    {
        if (arCamera == null || arMapOrigin == null || qrMapCalibrator == null)
        {
            Debug.LogError("IndoorPathVisualizer: Assign AR Camera, ARMapOrigin, and QRMapCalibrator.");
            enabled = false;
            return;
        }
        path = new NavMeshPath();
        destinationPoint = null;
        hasArrived = false;
        ClearArrows();
    }

    private void Update()
    {
        // Gate route recalculation AND arrival detection behind live AR safety.
        // Simply hiding the arrows in a UI script is insufficient: an unchecked
        // NavMesh update can otherwise accept a drifted camera position.
        UpdateTrackingSafety();
        if (IsRouteSafetyPaused || destinationPoint == null || hasArrived ||
            qrMapCalibrator == null || !qrMapCalibrator.IsCalibrated)
            return;

        if (Time.time >= nextRouteUpdate)
        {
            nextRouteUpdate = Time.time + Mathf.Max(0.1f, recalculateInterval);
            RecalculateRoute();
        }
    }

    public void SetDestination(Transform newDestination)
    {
        if (newDestination == null)
        {
            StopNavigation();
            return;
        }
        destinationPoint = newDestination;
        hasArrived = false;
        nextRouteUpdate = 0f;
        EstimatedRemainingMapMeters = -1f;
        HasValidRoute = false;
        LastRouteIssue = string.Empty;
        ClearArrows();
        if (qrMapCalibrator != null && qrMapCalibrator.IsCalibrated &&
            !IsRouteSafetyPaused)
            RecalculateRoute();
    }

    public void StopNavigation()
    {
        destinationPoint = null;
        hasArrived = false;
        nextRouteUpdate = 0f;
        EstimatedRemainingMapMeters = -1f;
        HasValidRoute = false;
        LastRouteIssue = string.Empty;
        ClearArrows();
    }

    public void RecalculateRoute()
    {
        if (IsRouteSafetyPaused)
        {
            ClearArrows();
            EstimatedRemainingMapMeters = -1f;
            return;
        }
        if (destinationPoint == null || hasArrived || path == null ||
            qrMapCalibrator == null || !qrMapCalibrator.IsCalibrated)
            return;

        // The camera is usually 1–2m ABOVE the walkable floor. Sampling at its
        // measured Y can select a different floor or miss narrow walkways. The
        // cross-floor controller already chooses a target on the CURRENT floor
        // (the final destination or the approach to the stairs), so sample
        // the camera's X/Z at that target's floor elevation.
        Vector3 routeTarget = GetNavTargetPosition(destinationPoint);
        Vector3 cameraInMap = arMapOrigin.InverseTransformPoint(arCamera.position);
        cameraInMap.y = routeTarget.y;
        const float sameFloorHeightTolerance = 0.65f;
        if (!NavMesh.SamplePosition(cameraInMap, out NavMeshHit startHit,
                navMeshSearchRadius, NavMesh.AllAreas) ||
            Mathf.Abs(startHit.position.y - routeTarget.y) > sameFloorHeightTolerance)
        {
            ReportRouteIssue("Your estimated position is outside the walkable area on this floor. Move to a walkway or rescan its QR.");
            return;
        }
        if (!NavMesh.SamplePosition(routeTarget, out NavMeshHit goalHit,
                navMeshSearchRadius, NavMesh.AllAreas) ||
            Mathf.Abs(goalHit.position.y - routeTarget.y) > sameFloorHeightTolerance)
        {
            ReportRouteIssue("Destination is not on this floor's walkable NavMesh. Check its destination marker and rebake if needed.");
            return;
        }
        if (!NavMesh.CalculatePath(startHit.position, goalHit.position,
                NavMesh.AllAreas, path) ||
            path.status != NavMeshPathStatus.PathComplete)
        {
            ReportRouteIssue("No complete walkway connects your position to this destination. Check gaps, doors and outdoor paths.");
            return;
        }
        foreach (Vector3 corner in path.corners)
        {
            if (Mathf.Abs(corner.y - goalHit.position.y) > sameFloorHeightTolerance)
            {
                ReportRouteIssue("Route unexpectedly crosses floors. Use the staircase QR transition instead.");
                return;
            }
        }
        HasValidRoute = true;
        LastRouteIssue = string.Empty;
        EstimatedRemainingMapMeters = PathLength(path);
        if (EstimatedRemainingMapMeters <= arrivalDistance)
        {
            HandleArrival();
            return;
        }

        if (showRouteTrail) RefreshArrowTrail();
        else ClearArrows();
    }

    private static float PathLength(NavMeshPath currentPath)
    {
        float result = 0f;
        Vector3[] corners = currentPath.corners;
        for (int i = 0; i + 1 < corners.Length; i++)
            result += Vector3.Distance(corners[i], corners[i + 1]);
        return result;
    }

    private void HandleArrival()
    {
        if (hasArrived || destinationPoint == null) return;
        hasArrived = true;
        EstimatedRemainingMapMeters = 0f;
        ClearArrows();
        Debug.Log("Destination reached: " + destinationPoint.name);
        DestinationReached?.Invoke(destinationPoint);
    }

    private void RefreshArrowTrail()
    {
        if (pathRendererParent == null || arrowPrefab == null || path == null)
        {
            ClearArrows();
            return;
        }

        Vector3[] corners = path.corners;
        if (corners.Length < 2)
        {
            ClearArrows();
            return;
        }

        float spacing = Mathf.Max(0.2f, arrowSpacing);
        float nextArrowAt = Mathf.Max(0.05f, firstArrowDistance);
        float traversed = 0f;
        int used = 0;
        int limit = Mathf.Max(1, maximumVisibleArrows);
        // Keep the arrow nose from entering a destination prop / doorway marker.
        // This affects rendering only. Arrival and route validity still use the
        // actual route endpoint (or the optional NavigationArrival child).
        float totalPathLength = PathLength(path);
        float lastArrowAt = Mathf.Max(0f, totalPathLength - arrowEndClearance);

        // One continuous spacing sequence across all corners (no repeated
        // overlap or unexpectedly wide gaps at hallway intersections).
        for (int segmentIndex = 0;
             segmentIndex + 1 < corners.Length && used < limit;
             segmentIndex++)
        {
            Vector3 a = corners[segmentIndex];
            Vector3 b = corners[segmentIndex + 1];
            Vector3 delta = b - a;
            delta.y = 0f;
            float length = delta.magnitude;
            if (length < 0.02f) continue;

            Vector3 dir = delta / length;
            while (nextArrowAt <= traversed + length &&
                   nextArrowAt <= lastArrowAt && used < limit)
            {
                float along = nextArrowAt - traversed;
                Vector3 mapPoint = a + dir * along;
                mapPoint.y = Mathf.Lerp(a.y, b.y, along / length) + arrowHeight;
                // Use a real-world horizontal heading. If the map origin or
                // parent transform carries an unexpected pitch/roll on iOS,
                // multiplying its quaternion into the arrow could tilt the
                // route mesh. Removing world Y from the heading is safe on
                // both platforms and does not change path coordinates.
                Vector3 worldDirection = arMapOrigin.TransformDirection(dir);
                worldDirection.y = 0f;
                if (worldDirection.sqrMagnitude < 0.0001f)
                {
                    nextArrowAt += spacing;
                    continue;
                }
                Quaternion worldRotation = Quaternion.LookRotation(
                    worldDirection.normalized, Vector3.up);

                GameObject arrow = GetArrowFromPool(used);
                if (arrow == null) break;

                // Do not assume ARPathRenderer has identity local transform.
                arrow.transform.position = arMapOrigin.TransformPoint(mapPoint);
                arrow.transform.rotation = worldRotation;
                arrow.transform.localScale = Vector3.one * arrowScale;
#if UNITY_IOS && !UNITY_EDITOR
                if (used == 0 && Time.time >= nextArrowDiagnosticAt)
                {
                    float tilt = Vector3.Angle(arrow.transform.up, Vector3.up);
                    Debug.Log("[SLRP AR][iOS] Arrow tilt=" + tilt.ToString("F1") +
                        " degrees, map rotation=" + arMapOrigin.eulerAngles +
                        ", world position=" + arrow.transform.position +
                        ", map floor Y=" + mapPoint.y.ToString("F2") +
                        ". A 0-degree tilt with misplaced arrows indicates " +
                        "calibration height/heading rather than mesh rotation.");
                    nextArrowDiagnosticAt = Time.time + 8f;
                }
#endif
                if (!arrow.activeSelf) arrow.SetActive(true);
                used++;
                nextArrowAt += spacing;
            }
            traversed += length;
        }

        for (int i = used; i < pooledArrows.Count; i++)
            if (pooledArrows[i] != null && pooledArrows[i].activeSelf)
                pooledArrows[i].SetActive(false);
    }

    private GameObject GetArrowFromPool(int index)
    {
        while (pooledArrows.Count <= index)
        {
            GameObject created = Instantiate(arrowPrefab, pathRendererParent);
            created.name = "Route_Crimson_Arrow_" + (pooledArrows.Count + 1).ToString("00");
            created.SetActive(false);
            pooledArrows.Add(created);
        }
        return pooledArrows[index];
    }

    public void ClearArrows()
    {
        foreach (GameObject arrow in pooledArrows)
            if (arrow != null && arrow.activeSelf)
                arrow.SetActive(false);
    }

    private void OnDestroy()
    {
        foreach (GameObject arrow in pooledArrows)
            if (arrow != null)
                Destroy(arrow);
        pooledArrows.Clear();
    }
}
