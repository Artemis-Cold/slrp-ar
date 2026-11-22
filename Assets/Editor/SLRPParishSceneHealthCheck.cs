#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Read-only validation against the CURRENT parish scene and serialized data.
/// Never moves, resizes or rebakes a user-calibrated object.
/// Runtime accuracy must still be tested on-site and on device.
/// </summary>
public static class SLRPParishSceneHealthCheck
{
    private static readonly string[] Destinations = {
        "F1_FunctionHall", "F1_MultipurposeHall", "F1_ParishOffice", "F1_ParishStore",
        "F1_BellTower", "F1_ComfortRoom", "F2_MainChurch", "F2_Altar"
    };
    private static readonly string[] Qrs = {
        "F1_LeftStairs_QR", "F1_RightStairs_QR", "F1_FunctionHall_QR",
        "F2_LeftStairs_QR", "F2_RightStairs_QR"
    };
    private static readonly string[] StairWaypoints = {
        "F1_LeftStairs_Approach", "F1_RightStairs_Approach",
        "F2_LeftStairs_Exit", "F2_RightStairs_Exit"
    };
    private const string LibraryPath = "Assets/ParishQRMarkers/ParishQRReferenceImageLibrary.asset";

    [MenuItem("Tools/SLRP AR/1 - Validate Parish Scene and QR Setup (read-only)")]
    public static void Validate()
    {
        if (!SLRPParishNavMeshWorkbench.TryGetFloors(out Transform first, out Transform second))
            return;

        List<string> errors = new List<string>();
        List<string> warnings = new List<string>();
        StringBuilder report = new StringBuilder();
        report.AppendLine("[SLRP AR] PARISH SCENE HEALTH CHECK (NO CHANGES)");
        Transform parish = first.parent;
        if (parish == null) { Debug.LogError("[SLRP AR] Missing ParishMap root."); return; }

        Transform destinations = parish.Find("ParishDestinations");
        Transform qrs = parish.Find("CalibrationPoints");
        Transform stairs = parish.Find("StairTransitions");
        InspectPositions("DESTINATIONS", destinations, Destinations, errors, report);
        InspectPositions("QR CHECKPOINTS", qrs, Qrs, errors, report);
        if (qrs != null)
        {
            if (qrs.Find("F1_BellTower_QR") != null || qrs.Find("F1_ComfortRoom_QR") != null)
                errors.Add("Retired exterior QR transforms still present. Run the Function Hall QR placement tool.");
            if (qrs.childCount != Qrs.Length)
                errors.Add("CalibrationPoints must contain exactly five checkpoint transforms.");
        }
        InspectPositions("STAIR WAYPOINTS", stairs, StairWaypoints, errors, report);
        CheckSurface("FIRST FLOOR", first, errors, warnings, report);
        CheckSurface("SECOND FLOOR", second, errors, warnings, report);

        XRReferenceImageLibrary library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(LibraryPath);
        if (library == null) errors.Add("Parish QR Reference Image Library is missing: " + LibraryPath);
        else
        {
            Dictionary<string, int> registered = new Dictionary<string, int>(StringComparer.Ordinal);
            report.AppendLine("QR REFERENCE IMAGE LIBRARY:");
            for (int i = 0; i < library.count; ++i)
            {
                var image = library[i];
                if (!registered.ContainsKey(image.name)) registered[image.name] = 0;
                registered[image.name]++;
                report.AppendLine("  " + image.name + " — physical size " + image.size.x.ToString("F3") +
                    " x " + image.size.y.ToString("F3") + " m");
            }
            foreach (string name in Qrs)
            {
                if (!registered.ContainsKey(name)) errors.Add("QR image not registered: " + name);
                else if (registered[name] != 1) errors.Add("Duplicate QR reference name: " + name);
                if (AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/ParishQRMarkers/" + name + ".png") == null)
                    errors.Add("QR PNG missing: " + name);
            }
            if (library.count != Qrs.Length)
                warnings.Add("Reference Image Library contains " + library.count +
                    " images; expected five parish checkpoints. Inspect extras before shipping.");
            warnings.Add("Reference-image physical width must match each displayed/printed marker. " +
                "The check above does not establish actual QR mounting height or heading.");
        }

        ARTrackedImageManager tracked = UnityEngine.Object.FindFirstObjectByType<ARTrackedImageManager>(FindObjectsInactive.Include);
        if (tracked == null) errors.Add("ARTrackedImageManager missing from the scene.");
        else
        {
            if (!tracked.enabled || !tracked.gameObject.activeInHierarchy)
                errors.Add("ARTrackedImageManager is disabled/inactive.");
            SerializedProperty serializedLibrary = new SerializedObject(tracked).FindProperty("m_SerializedLibrary");
            if (serializedLibrary == null)
                warnings.Add("Unable to inspect ARTrackedImageManager serialized library field automatically; check it in Inspector.");
            else if (serializedLibrary.objectReferenceValue == null)
                errors.Add("ARTrackedImageManager has no Reference Image Library assigned.");
            else if (library != null && serializedLibrary.objectReferenceValue != library)
                errors.Add("ARTrackedImageManager uses a different Reference Image Library than the parish library.");
        }

        RequireComponent<IndoorPathVisualizer>("IndoorPathVisualizer", errors);
        RequireComponent<QRMapCalibrator>("QRMapCalibrator", errors);
        RequireComponent<NavigationUIManager>("NavigationUIManager", errors);
        RequireComponent<ParishFloorNavigationController>("ParishFloorNavigationController", errors);
        RequireComponent<QRNavigationReadyUI>("QRNavigationReadyUI", errors);
        RequireComponent<WayfinderTrackingWarningUI>("QR/tracking warning UI", errors);

        Scene scene = SceneManager.GetActiveScene();
        int missingScripts = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Transform item in root.GetComponentsInChildren<Transform>(true))
            {
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(item.gameObject);
                if (missing == 0) continue;
                missingScripts += missing;
                errors.Add("Missing MonoBehaviour script on: " + FullPath(item));
            }
        report.AppendLine("Missing MonoBehaviour references: " + missingScripts);

        bool buildSceneEnabled = false;
        foreach (EditorBuildSettingsScene included in EditorBuildSettings.scenes)
            if (included.enabled && included.path == "Assets/Scenes/ParishARNavigation.unity")
                buildSceneEnabled = true;
        if (!buildSceneEnabled) errors.Add("ParishARNavigation is not enabled in Build Settings/Build Profiles.");

        report.AppendLine("\nERRORS (" + errors.Count + "):");
        if (errors.Count == 0) report.AppendLine("  None detected by the read-only structural checks.");
        foreach (string error in errors) report.AppendLine("  ERROR: " + error);
        report.AppendLine("\nNOTES (" + warnings.Count + "):");
        foreach (string warning in warnings) report.AppendLine("  CHECK: " + warning);
        report.AppendLine("\nNEXT: Run '0 - Bake BOTH Floors + Verify Routes', then '7 - Diagnose Parish Routes' " +
            "and '9 - Scene Route Arrow Preview'. A structurally valid scene is NOT proof of safe physical navigation.");
        if (errors.Count > 0) Debug.LogError(report.ToString());
        else Debug.Log(report.ToString());
        EditorUtility.DisplayDialog("SLRP AR Scene Health", errors.Count + " errors and " + warnings.Count +
            " notes. See Console for the full positions and component report. No scene objects were changed.", "OK");
    }

    private static void InspectPositions(string title, Transform root, string[] names,
        List<string> errors, StringBuilder report)
    {
        report.AppendLine(title + ":");
        if (root == null) { errors.Add("Missing scene group: " + title); return; }
        foreach (string name in names)
        {
            Transform found = root.Find(name);
            if (found == null) { errors.Add("Missing scene location: " + name); continue; }
            report.AppendLine("  " + name + "  world=" + found.position.ToString("F3") +
                "  yaw=" + found.eulerAngles.y.ToString("F1") + "°" +
                (found.gameObject.activeInHierarchy ? "" : " [INACTIVE]"));
        }
    }

    private static void CheckSurface(string title, Transform floor, List<string> errors,
        List<string> warnings, StringBuilder report)
    {
        NavMeshSurface surface = floor.GetComponent<NavMeshSurface>();
        if (surface == null) { errors.Add(title + ": NavMeshSurface missing."); return; }
        if (!surface.isActiveAndEnabled) errors.Add(title + ": NavMeshSurface disabled or inactive.");
        if (surface.navMeshData == null) errors.Add(title + ": no baked NavMeshData assigned.");
        if (surface.collectObjects != CollectObjects.Children)
            warnings.Add(title + ": Collect Objects = " + surface.collectObjects +
                "; check for wrong-floor geometry included in the bake.");
        report.AppendLine(title + ": data=" + (surface.navMeshData != null ? surface.navMeshData.name : "MISSING") +
            ", collect=" + surface.collectObjects + ", enabled=" + surface.isActiveAndEnabled);
    }

    private static void RequireComponent<T>(string name, List<string> errors) where T : Component
    {
        T[] components = UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (components.Length == 0) errors.Add("Required runtime component absent: " + name);
        else if (components.Length > 1) errors.Add("Multiple " + name + " components found; check which one is active.");
        else if (components[0].gameObject.scene != SceneManager.GetActiveScene())
            errors.Add("Required component is not in the active parish scene: " + name);
    }

    private static string FullPath(Transform item)
    {
        string result = item.name;
        while (item.parent != null) { item = item.parent; result = item.name + "/" + result; }
        return result;
    }
}
#endif
