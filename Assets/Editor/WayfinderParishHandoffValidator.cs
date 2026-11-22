#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.XR.ARSubsystems;

/// <summary>Non-destructive checklist. Does NOT rebake or reposition anything.</summary>
public static class WayfinderParishHandoffValidator
{
    [MenuItem("Tools/SLRP AR/6 - Validate Parish Cross-Floor Setup")]
    public static void Validate()
    {
        List<string> errors = new List<string>();
        List<string> notes = new List<string>();
        GameObject map = GameObject.Find("NavigationMap");
        Transform parish = map != null ? map.transform.Find("ParishMap") : null;
        if (parish == null)
        {
            EditorUtility.DisplayDialog("SLRP AR validation", "ParishMap not found in the open scene.", "OK");
            return;
        }
        Transform stairs = parish.Find("StairTransitions");
        Transform destinations = parish.Find("ParishDestinations");
        string[] required = {
            "F1_LeftStairs_Approach", "F1_RightStairs_Approach",
            "F2_LeftStairs_Exit", "F2_RightStairs_Exit"};
        foreach (string name in required)
        {
            Transform point = stairs != null ? stairs.Find(name) : null;
            if (point == null) errors.Add("Missing waypoint: " + name);
            else notes.Add(name + " at " + point.position.ToString("F2"));
        }

        ParishFloorNavigationController cross = Object.FindAnyObjectByType<ParishFloorNavigationController>();
        if (cross == null) errors.Add("Missing ParishFloorNavigationController on NavigationUIManager.");

        XRReferenceImageLibrary library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(
            "Assets/ParishQRMarkers/ParishQRReferenceImageLibrary.asset");
        string[] qrNames = {"F1_LeftStairs_QR", "F1_RightStairs_QR", "F2_LeftStairs_QR",
            "F2_RightStairs_QR", "F1_FunctionHall_QR"};
        if (library == null) errors.Add("Missing parish AR Reference Image Library asset.");
        else
        {
            HashSet<string> names = new HashSet<string>();
            for (int i = 0; i < library.count; i++) names.Add(library[i].name);
            foreach (string n in qrNames)
            {
                if (!names.Contains(n)) errors.Add("QR not registered in parish library: " + n);
                if (AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/ParishQRMarkers/" + n + ".png") == null)
                    errors.Add("Missing PNG texture: " + n);
            }
            notes.Add("QR library contains " + library.count + " images.");
        }

        CheckRoute(destinations, stairs, "F1_ParishOffice", "F1_LeftStairs_Approach", errors, notes);
        CheckRoute(destinations, stairs, "F1_ParishOffice", "F1_RightStairs_Approach", errors, notes);
        CheckRoute(destinations, stairs, "F2_MainChurch", "F2_LeftStairs_Exit", errors, notes);
        CheckRoute(destinations, stairs, "F2_MainChurch", "F2_RightStairs_Exit", errors, notes);

        string report = (errors.Count == 0 ? "No static problems detected.\n" :
            errors.Count + " item(s) need attention:\n- " + string.Join("\n- ", errors) + "\n") +
            "\n" + string.Join("\n", notes) +
            "\n\nThis check is NOT a substitute for real-device QR and staircase testing.";
        if (errors.Count > 0) Debug.LogWarning("[SLRP AR validation] " + report);
        else Debug.Log("[SLRP AR validation] " + report);
        EditorUtility.DisplayDialog("Parish cross-floor validation", report, "OK");
    }

    private static void CheckRoute(Transform destinations, Transform stairs,
        string destinationName, string waypointName, List<string> errors, List<string> notes)
    {
        Transform a = destinations != null ? destinations.Find(destinationName) : null;
        Transform b = stairs != null ? stairs.Find(waypointName) : null;
        if (a == null || b == null) return;
        if (!NavMesh.SamplePosition(a.position, out NavMeshHit start, 1.2f, NavMesh.AllAreas) ||
            !NavMesh.SamplePosition(b.position, out NavMeshHit end, 1.2f, NavMesh.AllAreas) ||
            Mathf.Abs(start.position.y - end.position.y) > 0.65f)
        {
            errors.Add("Waypoint or destination not on its floor NavMesh: " + waypointName);
            return;
        }
        NavMeshPath path = new NavMeshPath();
        if (!NavMesh.CalculatePath(start.position, end.position, NavMesh.AllAreas, path) ||
            path.status != NavMeshPathStatus.PathComplete)
            errors.Add("No complete NavMesh route from " + destinationName + " to " + waypointName);
        else notes.Add("Complete route: " + destinationName + " -> " + waypointName);
    }
}
#endif
