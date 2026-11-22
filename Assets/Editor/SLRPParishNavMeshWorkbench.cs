#if UNITY_EDITOR
using System;
using System.Text;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// Safe first/second floor bake and verification entry point.
/// Does not relocate/reparent/resize any user-calibrated geometry or QR markers.
/// </summary>
public static class SLRPParishNavMeshWorkbench
{
    private const string SceneName = "ParishARNavigation";

    [MenuItem("Tools/SLRP AR/0 - Inspect Parish NavMesh Setup (no changes)")]
    public static void Inspect()
    {
        if (!TryGetFloors(out Transform first, out Transform second)) return;
        StringBuilder report = new StringBuilder();
        report.AppendLine("[SLRP AR] NAVMESH SETUP INSPECTION (read-only)");
        DescribeSurface("FIRST FLOOR", first, report);
        DescribeSurface("SECOND FLOOR", second, report);
        report.AppendLine("Existing baked NavMesh: " + (NavMesh.CalculateTriangulation().vertices.Length > 0 ?
            "contains vertices" : "EMPTY: bake both floor surfaces before route verification"));
        report.AppendLine("Do not use the old fixed-coordinate outdoor test for moved checkpoints.");
        report.AppendLine("The inspection does not move markers, destinations, walls, or stairs.");
        Debug.Log(report.ToString());
        EditorUtility.DisplayDialog("SLRP AR NavMesh inspection",
            "Setup inspection written to Console. Review the warnings, then use '0 - Bake BOTH Floors + Verify Routes'.", "OK");
    }

    [MenuItem("Tools/SLRP AR/0 - Bake BOTH Floors + Verify Routes")]
    public static void BakeBothFloorsAndAudit()
    {
        if (!TryGetFloors(out Transform first, out Transform second)) return;
        NavMeshSurface firstSurface = first.GetComponent<NavMeshSurface>();
        NavMeshSurface secondSurface = second.GetComponent<NavMeshSurface>();
        if (firstSurface == null || secondSurface == null)
        {
            EditorUtility.DisplayDialog("SLRP AR: NavMeshSurface missing",
                "Both FirstFloor_BLOCKOUT and SecondFloor_BLOCKOUT must have NavMeshSurface components. " +
                "No changes were made.", "OK");
            return;
        }
        if (!firstSurface.isActiveAndEnabled || !secondSurface.isActiveAndEnabled)
        {
            EditorUtility.DisplayDialog("SLRP AR: surface disabled",
                "Enable both floor objects and their NavMeshSurface components, then retry. " +
                "No objects were moved or modified.", "OK");
            return;
        }
        if (!EditorUtility.DisplayDialog("Rebake parish walkable areas?",
            "This rebuilds BOTH floor NavMeshSurface assets using the CURRENT scene geometry. " +
            "It will NOT move destinations, QR markers, calibrated rotations, walls, or stair waypoints.\n\n" +
            "Your revised scene positions will be kept and the scene will be saved. " +
            "Please ensure you have a backup first.\n\n" +
            "After baking, the route audit will show any disconnected destinations; it cannot invent missing physical doorways.",
            "Bake both + audit", "Cancel")) return;

        try
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("[SLRP AR] PARISH NAVMESH REBUILD");
            DescribeSurface("FIRST FLOOR - before", first, report);
            DescribeSurface("SECOND FLOOR - before", second, report);
            EditorUtility.DisplayProgressBar("SLRP AR NavMesh", "Baking FIRST floor from updated geometry...", 0.2f);
            firstSurface.BuildNavMesh();
            if (firstSurface.navMeshData == null)
                throw new InvalidOperationException("First-floor bake produced no NavMeshData. Check the floor surface layer mask and source geometry.");
            EditorUtility.DisplayProgressBar("SLRP AR NavMesh", "Baking SECOND floor from updated geometry...", 0.55f);
            secondSurface.BuildNavMesh();
            if (secondSurface.navMeshData == null)
                throw new InvalidOperationException("Second-floor bake produced no NavMeshData. Check the floor surface layer mask and source geometry.");

            // BuildNavMesh maintains its own NavMeshData registration; do not add
            // NavMeshLinks across stairwells or force-connect any disconnected rooms.
            EditorUtility.DisplayProgressBar("SLRP AR NavMesh", "Saving baked assets and running route checks...", 0.85f);
            AssetDatabase.SaveAssets();
            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                Debug.LogWarning("[SLRP AR] NavMesh was rebuilt, but Unity could not save the scene automatically. Save manually.");
            DescribeSurface("FIRST FLOOR - after", first, report);
            DescribeSurface("SECOND FLOOR - after", second, report);
            report.AppendLine("Loaded NavMesh vertices: " + NavMesh.CalculateTriangulation().vertices.Length);
            Debug.Log(report.ToString());
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("SLRP AR NavMesh bake interrupted",
                "Unity reported an error. See Console. Do not deploy until both floor bakes and route checks pass. " +
                "Your updated marker/destination positions were not intentionally changed.", "OK");
            return;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
        WayfinderParishRouteAudit.Diagnose();
    }

    public static bool TryGetParishRoot(out Transform parish)
    {
        parish = null;
        if (Application.isPlaying || SceneManager.GetActiveScene().name != SceneName)
        {
            EditorUtility.DisplayDialog("SLRP AR: open parish scene",
                "Open Assets/Scenes/ParishARNavigation.unity and EXIT Play Mode before using these tools.", "OK");
            return false;
        }
        GameObject map = GameObject.Find("NavigationMap");
        parish = map != null ? map.transform.Find("ParishMap") : null;
        if (parish == null)
        {
            EditorUtility.DisplayDialog("SLRP AR: parish hierarchy missing",
                "Could not find NavigationMap/ParishMap in the current active scene. No changes made.", "OK");
            return false;
        }
        return true;
    }

    public static bool TryGetFloors(out Transform first, out Transform second)
    {
        first = null;
        second = null;
        if (!TryGetParishRoot(out Transform parish)) return false;
        first = parish.Find("FirstFloor_BLOCKOUT");
        second = parish.Find("SecondFloor_BLOCKOUT");
        if (first == null || second == null)
        {
            EditorUtility.DisplayDialog("SLRP AR: floor objects missing",
                "Expected FirstFloor_BLOCKOUT and SecondFloor_BLOCKOUT beneath NavigationMap/ParishMap. " +
                "Check renamed or moved floor objects. Nothing was changed.", "OK");
            return false;
        }
        return true;
    }

    private static void DescribeSurface(string title, Transform root, StringBuilder report)
    {
        NavMeshSurface surface = root.GetComponent<NavMeshSurface>();
        if (surface == null)
        {
            report.AppendLine(title + ": MISSING NavMeshSurface component");
            return;
        }
        int includedRenderers = 0, includedColliders = 0;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            if (r.gameObject.activeInHierarchy && (surface.layerMask.value & (1 << r.gameObject.layer)) != 0)
                includedRenderers++;
        foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
            if (c.enabled && c.gameObject.activeInHierarchy && (surface.layerMask.value & (1 << c.gameObject.layer)) != 0)
                includedColliders++;
        report.AppendLine(title + ": " + root.name +
            ", enabled=" + surface.isActiveAndEnabled +
            ", collect=" + surface.collectObjects +
            ", geometry=" + surface.useGeometry +
            ", layerMask=" + surface.layerMask.value +
            ", included renderers=" + includedRenderers +
            ", included colliders=" + includedColliders +
            ", NavMeshData=" + (surface.navMeshData == null ? "MISSING" : surface.navMeshData.name));
        if (surface.useGeometry == NavMeshCollectGeometry.RenderMeshes && includedRenderers == 0)
            report.AppendLine("  WARNING: no active included renderers under this floor; its bake may be empty. Check layer mask / hierarchy.");
        if (surface.useGeometry == NavMeshCollectGeometry.PhysicsColliders && includedColliders == 0)
            report.AppendLine("  WARNING: no active included colliders under this floor; its bake may be empty. Check layer mask / hierarchy.");
        if (surface.collectObjects != CollectObjects.Children)
            report.AppendLine("  CHECK: collectObjects is " + surface.collectObjects +
                ". Ensure each floor includes ONLY its intended walkable geometry; accidental cross-floor collection breaks routes.");
    }
}
#endif
