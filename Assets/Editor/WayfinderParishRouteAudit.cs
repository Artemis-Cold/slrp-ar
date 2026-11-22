#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Non-destructive parish NavMesh connectivity audit. Never moves objects,
/// bakes the NavMesh, or edits the existing scene.
/// A static reference-point check is NOT proof of accuracy on a real phone.
/// </summary>
public static class WayfinderParishRouteAudit
{
    private const float SearchRadius = 2.0f;
    private const float HeightTolerance = 0.65f;

    [MenuItem("Tools/SLRP AR/7 - Diagnose Parish Routes (no rebake)")]
    public static void Diagnose()
    {
        GameObject map = GameObject.Find("NavigationMap");
        Transform parish = map == null ? null : map.transform.Find("ParishMap");
        if (parish == null)
        {
            EditorUtility.DisplayDialog("SLRP AR route audit", "Open ParishARNavigation first. NavigationMap/ParishMap is missing.", "OK");
            return;
        }

        Transform destinations = parish.Find("ParishDestinations");
        Transform transitions = parish.Find("StairTransitions");
        Transform markers = parish.Find("CalibrationPoints");
        if (destinations == null || transitions == null || markers == null)
        {
            EditorUtility.DisplayDialog("SLRP AR route audit", "ParishDestinations, StairTransitions, or CalibrationPoints is missing.", "OK");
            return;
        }

        // Audit the exact expected matrix: five floor-aware test starts across eight
        // destinations (22 routes), plus four staircase handoff checks (26 total).
        // A missing Transform counts as a FAILURE instead of silently reducing
        // the denominator and reporting a misleading clean audit.
        string[] requiredDestinations = {
            "F1_FunctionHall", "F1_MultipurposeHall", "F1_ParishOffice", "F1_ParishStore",
            "F1_BellTower", "F1_ComfortRoom", "F2_MainChurch", "F2_Altar"
        };
        string[] requiredStarts = {
            "F1_LeftStairs_QR", "F1_RightStairs_QR",
            "F1_FunctionHall_QR",
            "F2_LeftStairs_QR", "F2_RightStairs_QR"
        };

        Transform[] starts = {
            markers.Find(requiredStarts[0]), markers.Find(requiredStarts[1]),
            markers.Find(requiredStarts[2]),
            markers.Find(requiredStarts[3]), markers.Find(requiredStarts[4])
        };

        StringBuilder output = new StringBuilder();
        output.AppendLine("[SLRP AR] PARISH ROUTE AUDIT — loaded NavMesh; scene is unchanged");
        output.AppendLine("All 26 expected test routes are counted, including missing markers.");
        output.AppendLine("Static checkpoint positions are not live AR camera positions.");
        int good = 0, bad = 0;
        foreach (string goalName in requiredDestinations)
        {
            Transform goal = destinations.Find(goalName);
            string goalFloor = FloorPrefix(goalName);
            int reachable = 0, tested = 0;
            List<string> failures = new List<string>();
            for (int i = 0; i < starts.Length; i++)
            {
                if (FloorPrefix(requiredStarts[i]) != goalFloor) continue;
                tested++;
                string issue;
                if (goal == null) issue = "Destination Transform missing";
                else if (starts[i] == null) issue = "Start waypoint/QR Transform missing";
                else issue = TestRoute(IndoorPathVisualizer.GetNavStartPosition(starts[i]),
                    IndoorPathVisualizer.GetNavTargetPosition(goal));
                if (issue == null) { reachable++; good++; }
                else { bad++; failures.Add(requiredStarts[i] + ": " + issue); }
            }
            output.AppendLine(goalName + "  reachable " + reachable + "/" + tested + " test starts");
            foreach (string failure in failures)
                output.AppendLine("    FAIL " + failure);
        }

        // Both alternatives of the cross-floor handoff must be possible on their
        // own floors. These tests do not imply a stair NavMeshLink exists.
        VerifyWaypointPair(transitions, destinations, "F1_LeftStairs_Approach", "F1_ParishOffice", output, ref good, ref bad);
        VerifyWaypointPair(transitions, destinations, "F1_RightStairs_Approach", "F1_ParishOffice", output, ref good, ref bad);
        VerifyWaypointPair(transitions, destinations, "F2_LeftStairs_Exit", "F2_MainChurch", output, ref good, ref bad);
        VerifyWaypointPair(transitions, destinations, "F2_RightStairs_Exit", "F2_MainChurch", output, ref good, ref bad);

        output.AppendLine("PASS " + good + " / FAIL " + bad + ".");
        output.AppendLine("If a destination fails: check its marker, doorway gaps, walkway connectivity, and the two NavMeshSurface bakes.");
        output.AppendLine("Do not move walls or markers based only on this audit; verify the real parish layout.");
        Debug.Log(output.ToString());
        EditorUtility.DisplayDialog("Parish route audit", "Passed " + good + " checks, failed " + bad +
            ". Full destination-by-destination results were printed in the Console.", "OK");
    }

    private static void VerifyWaypointPair(Transform stairs, Transform destinations,
        string waypointName, string destinationName, StringBuilder output, ref int good, ref int bad)
    {
        Transform a = stairs.Find(waypointName);
        Transform b = destinations.Find(destinationName);
        if (a == null || b == null)
        {
            output.AppendLine("MISSING stair/destination: " + waypointName + " -> " + destinationName);
            bad++;
            return;
        }
        string problem = TestRoute(IndoorPathVisualizer.GetNavStartPosition(a),
            IndoorPathVisualizer.GetNavTargetPosition(b));
        if (problem == null) { good++; output.AppendLine("PASS stair connection " + waypointName + " -> " + destinationName); }
        else { bad++; output.AppendLine("FAIL stair connection " + waypointName + " -> " + destinationName + ": " + problem); }
    }

    private static string TestRoute(Vector3 start, Vector3 end)
    {
        // Project the reference start to the destination's known walkable Y,
        // just as the runtime navigator does for a handheld AR camera.
        start.y = end.y;
        if (!NavMesh.SamplePosition(start, out NavMeshHit a, SearchRadius, NavMesh.AllAreas) ||
            Mathf.Abs(a.position.y - end.y) > HeightTolerance)
            return "Start outside this floor's NavMesh";
        if (!NavMesh.SamplePosition(end, out NavMeshHit b, SearchRadius, NavMesh.AllAreas) ||
            Mathf.Abs(b.position.y - end.y) > HeightTolerance)
            return "Destination outside this floor's NavMesh";
        NavMeshPath path = new NavMeshPath();
        if (!NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) ||
            path.status != NavMeshPathStatus.PathComplete)
            return "No connected path (" + path.status + ")";
        foreach (Vector3 corner in path.corners)
            if (Mathf.Abs(corner.y - b.position.y) > HeightTolerance)
                return "Path passes through an incorrect floor";
        return null;
    }

    private static string FloorPrefix(string name)
    {
        if (name.StartsWith("F1_")) return "F1";
        if (name.StartsWith("F2_")) return "F2";
        return string.Empty;
    }
}
#endif
