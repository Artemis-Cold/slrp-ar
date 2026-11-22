using System;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// QR-confirmed staircase handoff. There are NO NavMeshLinks across the stairs:
/// navigate on the current floor to a manually placed approach waypoint, stop
/// floor arrows, and resume the ORIGINAL destination only after a QR on the
/// destination floor is calibrated. Stairs are never selectable destinations.
/// </summary>
public class ParishFloorNavigationController : MonoBehaviour
{
    private enum Phase { Idle, Planning, ToStairs, WaitingForNextFloorQr, ToDestination }

    [Header("SLRP AR components")]
    [SerializeField] private IndoorPathVisualizer navigator;
    [SerializeField] private QRMapCalibrator calibrator;
    [SerializeField] private Transform arMapOrigin;
    [SerializeField] private Transform arCamera;
    [SerializeField] private NavigationUIManager navigationUI;

    [Header("User-positioned staircase transitions (DO NOT MOVE)")]
    [SerializeField] private Transform f1LeftApproach;
    [SerializeField] private Transform f1RightApproach;
    [SerializeField] private Transform f2LeftExit;
    [SerializeField] private Transform f2RightExit;

    [Header("Same-floor NavMesh checks")]
    [SerializeField, Min(0.2f)] private float sampleRadius = 1.2f;
    [SerializeField, Min(0.2f)] private float maximumFloorHeightError = 0.65f;

    private Phase phase = Phase.Idle;
    private Transform requestedDestination;
    private Transform activeStairWaypoint;
    private string desiredFloor = string.Empty;
    private string currentSide = string.Empty;
    private float nextPlanAttempt;
    private string planProblem = string.Empty;
    private bool awaitingFreshCalibration;

    public bool IsHeadingToStairs => phase == Phase.ToStairs;
    public bool IsWaitingForNextFloorQr => phase == Phase.WaitingForNextFloorQr;
    public bool HasPendingDestination => requestedDestination != null;
    public Transform RequestedDestination => requestedDestination;
    public string CurrentSide => currentSide;
    public string TargetFloorName => desiredFloor == "F2" ? "second floor" : "first floor";
    public string StatusDetail => planProblem;

    private void Awake()
    {
        if (navigator == null) navigator = FindAnyObjectByType<IndoorPathVisualizer>();
        if (calibrator == null) calibrator = FindAnyObjectByType<QRMapCalibrator>();
        if (navigationUI == null) navigationUI = GetComponent<NavigationUIManager>();
        if (arMapOrigin == null)
        {
            GameObject origin = GameObject.Find("ARMapOrigin");
            if (origin != null) arMapOrigin = origin.transform;
        }
        if (arCamera == null && Camera.main != null) arCamera = Camera.main.transform;
        if (arCamera == null)
        {
            Camera sceneCamera = FindAnyObjectByType<Camera>();
            if (sceneCamera != null) arCamera = sceneCamera.transform;
        }
        // Only fill missing references; never overwrite manually adjusted points.
        GameObject map = GameObject.Find("NavigationMap");
        Transform group = map != null ? map.transform.Find("ParishMap/StairTransitions") : null;
        if (group != null)
        {
            if (f1LeftApproach == null) f1LeftApproach = group.Find("F1_LeftStairs_Approach");
            if (f1RightApproach == null) f1RightApproach = group.Find("F1_RightStairs_Approach");
            if (f2LeftExit == null) f2LeftExit = group.Find("F2_LeftStairs_Exit");
            if (f2RightExit == null) f2RightExit = group.Find("F2_RightStairs_Exit");
        }
    }

    public void SelectDestination(Transform destination)
    {
        if (destination == null) { CancelRoute(); return; }
        requestedDestination = destination;
        desiredFloor = FloorFromName(destination.name);
        activeStairWaypoint = null;
        currentSide = string.Empty;
        planProblem = string.Empty;
        awaitingFreshCalibration = false;
        phase = Phase.Planning;
        if (navigator != null) navigator.StopNavigation();
        nextPlanAttempt = 0f;
        if (string.IsNullOrEmpty(desiredFloor))
        {
            planProblem = "Destination has no F1_ or F2_ floor prefix.";
            Debug.LogWarning("SLRP AR floors: " + planProblem + " " + destination.name);
            return;
        }
        TryPlan();
    }

    public void CancelRoute()
    {
        requestedDestination = null;
        desiredFloor = string.Empty;
        activeStairWaypoint = null;
        currentSide = string.Empty;
        planProblem = string.Empty;
        phase = Phase.Idle;
        awaitingFreshCalibration = false;
        if (navigator != null) navigator.StopNavigation();
    }

    /// <summary>Called by the permanent RESCAN QR action before clearing AR.</summary>
    public void NotifyRescanStarted()
    {
        if (requestedDestination == null) return;
        // Retain the user's selected FINAL destination. The existing UI resets
        // its displayed dropdown to "Select destination" during QR rescanning;
        // we restore that selection after QR calibration succeeds.
        if (phase != Phase.WaitingForNextFloorQr) phase = Phase.Planning;
        awaitingFreshCalibration = true;
        nextPlanAttempt = 0f;
        planProblem = string.Empty;
        if (navigator != null) navigator.StopNavigation();
    }

    private void Update()
    {
        if (requestedDestination == null || calibrator == null) return;
        if (!calibrator.IsCalibrated) return;

        if (phase == Phase.WaitingForNextFloorQr)
        {
            // The first-floor QR is still calibrated until RESCAN QR is tapped.
            // Never treat it as proof that the user already climbed the stairs.
            if (awaitingFreshCalibration && FloorFromName(calibrator.LastCalibratedMarker) == desiredFloor)
            {
                awaitingFreshCalibration = false;
                phase = Phase.Planning;
                nextPlanAttempt = 0f;
            }
            else if (!awaitingFreshCalibration)
                return;
            else if (FloorFromName(calibrator.LastCalibratedMarker) != desiredFloor)
            {
                planProblem = "That QR is on a different floor. Rescan a QR on the " + TargetFloorName + ".";
                return;
            }
        }

        if (phase == Phase.Planning && Time.time >= nextPlanAttempt)
        {
            nextPlanAttempt = Time.time + 1f;
            TryPlan();
        }
    }

    /// <summary>Consume arrival at an internal stair waypoint, not a parish destination.</summary>
    public bool TryHandleWaypointArrival(Transform reached)
    {
        if (phase != Phase.ToStairs || reached == null || reached != activeStairWaypoint)
            return false;
        if (navigator != null) navigator.StopNavigation();
        phase = Phase.WaitingForNextFloorQr;
        awaitingFreshCalibration = false;
        planProblem = string.Empty;
        Debug.Log("[SLRP AR] At " + currentSide + " stairs. Proceed to " + TargetFloorName +
                  ", then press RESCAN QR and scan the registered marker. Floor arrows paused.");
        return true;
    }

    public void NotifyFinalArrival(Transform destination)
    {
        if (phase != Phase.ToDestination || requestedDestination != destination) return;
        requestedDestination = null;
        activeStairWaypoint = null;
        currentSide = string.Empty;
        desiredFloor = string.Empty;
        phase = Phase.Idle;
    }

    private void TryPlan()
    {
        if (requestedDestination == null || navigator == null || calibrator == null ||
            arMapOrigin == null || arCamera == null || !calibrator.IsCalibrated) return;
        if (navigator.IsRouteSafetyPaused) return;

        string markerFloor = FloorFromName(calibrator.LastCalibratedMarker);
        if (string.IsNullOrEmpty(markerFloor))
        {
            planProblem = "Scan a registered parish QR to identify your floor.";
            return;
        }

        // When a QR is rescanned after climbing the stairs, the original
        // destination is automatically restored, without selecting it again.
        if (markerFloor == desiredFloor)
        {
            // IMPORTANT: Previously this selected a same-floor destination
            // WITHOUT checking connectivity. The UI could then say "Locating
            // route" indefinitely for unreachable rooms or disconnected paths.
            float distance = SameFloorRouteLength(requestedDestination);
            if (float.IsInfinity(distance))
            {
                SetPlanProblem("No complete walking path to " + requestedDestination.name +
                    " on this floor. Check its NavMesh, doorway and destination marker.");
                return; // Stay in Planning so a corrected position can retry.
            }
            phase = Phase.ToDestination;
            activeStairWaypoint = null;
            planProblem = string.Empty;
            if (navigationUI != null) navigationUI.RestoreDestinationSelection(requestedDestination);
            navigator.SetDestination(requestedDestination);
            Debug.Log("[SLRP AR] Route to " + requestedDestination.name +
                      " on " + markerFloor + " (" + distance.ToString("F1") + " map units).");
            return;
        }

        // Pick the shorter VERIFIED approach on the CURRENT floor. Stairs are
        // NOT joined with a NavMeshLink: arrows must stop before the stairwell.
        Transform left = markerFloor == "F1" ? f1LeftApproach : f2LeftExit;
        Transform right = markerFloor == "F1" ? f1RightApproach : f2RightExit;
        Transform farLeft = markerFloor == "F1" ? f2LeftExit : f1LeftApproach;
        Transform farRight = markerFloor == "F1" ? f2RightExit : f1RightApproach;
        // Check BOTH floor portions. An upstairs QR exit could be isolated from
        // the final room even when its downstairs approach is reachable.
        Vector3 finalArrival = IndoorPathVisualizer.GetNavTargetPosition(requestedDestination);
        float leftDistance = AddRouteLengths(SameFloorRouteLength(left),
            farLeft != null ? SameFloorSegmentLength(farLeft.position, finalArrival) : float.PositiveInfinity);
        float rightDistance = AddRouteLengths(SameFloorRouteLength(right),
            farRight != null ? SameFloorSegmentLength(farRight.position, finalArrival) : float.PositiveInfinity);
        Transform choice = leftDistance <= rightDistance ? left : right;
        float chosenLength = Mathf.Min(leftDistance, rightDistance);
        if (choice == null || float.IsInfinity(chosenLength))
        {
            SetPlanProblem("No complete walkable route through either staircase from " + markerFloor +
                ". Check both stair waypoints and the target floor's NavMesh.");
            return;
        }

        activeStairWaypoint = choice;
        currentSide = choice == left ? "left" : "right";
        planProblem = string.Empty;
        phase = Phase.ToStairs;
        if (navigationUI != null) navigationUI.RestoreDestinationSelection(requestedDestination);
        navigator.SetDestination(choice);
        Debug.Log("[SLRP AR] Route via " + markerFloor + " " + currentSide +
                  " stairs, estimated approach " + chosenLength.ToString("F1") + " map units.");
    }

    private void SetPlanProblem(string message)
    {
        if (planProblem != message) Debug.LogWarning("[SLRP AR floors] " + message);
        planProblem = message;
    }

    private float SameFloorRouteLength(Transform waypoint)
    {
        if (waypoint == null) return float.PositiveInfinity;
        Vector3 mapCamera = arMapOrigin.InverseTransformPoint(arCamera.position);
        // A handheld camera is typically 1-2 m ABOVE the walkable NavMesh.
        // Project only the Y coordinate to the waypoint's known floor before
        // sampling. Otherwise a strict same-floor height check rejects every
        // normal standing user, even with perfectly baked NavMeshes.
        Vector3 finalArrival = IndoorPathVisualizer.GetNavTargetPosition(waypoint);
        mapCamera.y = finalArrival.y;
        return SameFloorSegmentLength(mapCamera, finalArrival);
    }

    private static float AddRouteLengths(float a, float b)
    {
        return float.IsInfinity(a) || float.IsInfinity(b) ? float.PositiveInfinity : a + b;
    }

    private float SameFloorSegmentLength(Vector3 from, Vector3 to)
    {
        if (!TrySampleSameFloor(from, out NavMeshHit start) ||
            !TrySampleSameFloor(to, out NavMeshHit goal))
            return float.PositiveInfinity;
        if (Mathf.Abs(start.position.y - goal.position.y) > maximumFloorHeightError)
            return float.PositiveInfinity;
        NavMeshPath result = new NavMeshPath();
        if (!NavMesh.CalculatePath(start.position, goal.position, NavMesh.AllAreas, result)
            || result.status != NavMeshPathStatus.PathComplete) return float.PositiveInfinity;
        float distance = 0f;
        Vector3[] corners = result.corners;
        for (int i = 1; i < corners.Length; i++)
        {
            if (Mathf.Abs(corners[i].y - start.position.y) > maximumFloorHeightError)
                return float.PositiveInfinity; // Never use a hidden cross-floor detour.
            distance += Vector3.Distance(corners[i - 1], corners[i]);
        }
        // Unity can return a single/no corner when already at a destination.
        // This is an arrival, not an unreachable path.
        if (corners.Length < 2)
            return Vector3.Distance(start.position, goal.position) <= 0.5f
                ? Vector3.Distance(start.position, goal.position)
                : float.PositiveInfinity;
        return distance;
    }

    private bool TrySampleSameFloor(Vector3 pos, out NavMeshHit hit)
    {
        return NavMesh.SamplePosition(pos, out hit, sampleRadius, NavMesh.AllAreas) &&
               Mathf.Abs(hit.position.y - pos.y) < maximumFloorHeightError;
    }

    private static string FloorFromName(string markerOrDestinationName)
    {
        if (string.IsNullOrEmpty(markerOrDestinationName)) return string.Empty;
        if (markerOrDestinationName.StartsWith("F1_", StringComparison.OrdinalIgnoreCase)) return "F1";
        if (markerOrDestinationName.StartsWith("F2_", StringComparison.OrdinalIgnoreCase)) return "F2";
        return string.Empty;
    }
}
