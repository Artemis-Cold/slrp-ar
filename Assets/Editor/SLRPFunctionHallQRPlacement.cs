#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>Explicit, undoable migration of the binary parish scene. Does not move any existing destinations or stairs.</summary>
public sealed class SLRPFunctionHallQRPlacement : EditorWindow
{
    internal const string MarkerName = "F1_FunctionHall_QR";
    internal const string LibraryPath = "Assets/ParishQRMarkers/ParishQRReferenceImageLibrary.asset";
    internal static readonly string[] Retired = { "F1_BellTower_QR", "F1_ComfortRoom_QR" };
    internal static readonly string[] Required = { "F1_LeftStairs_QR", "F1_RightStairs_QR", "F2_LeftStairs_QR", "F2_RightStairs_QR", MarkerName };
    private readonly string[] firstFloorReferences = { "F1_LeftStairs_QR", "F1_RightStairs_QR" };
    private int referenceIndex;
    private float floorY, mountingHeight = 1.2f, standDistance = 0.9f;
    private bool initialized, picking, hasPoint;
    private Vector3 wallPoint, wallNormal;
    private string wallName = "";
    private string status = "Open the parish scene and pick the actual wall face to the LEFT of the middle Function Hall entrance.";

    [MenuItem("Tools/SLRP AR/10 - Place Function Hall QR")]
    private static void Open() => GetWindow<SLRPFunctionHallQRPlacement>("SLRP AR QR Placement").Show();

    internal static Transform FindParish(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            if (root.name == "NavigationMap") return root.transform.Find("ParishMap");
        // Tolerate a container above NavigationMap without selecting a different scene.
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == "NavigationMap") return t.Find("ParishMap");
        return null;
    }

    private bool TryContext(out Transform parish, out Transform markers, out Transform stair)
    {
        parish = null; markers = null; stair = null;
        Scene scene = SceneManager.GetActiveScene();
        if (Application.isPlaying || scene.name != "ParishARNavigation") return false;
        parish = FindParish(scene);
        markers = parish == null ? null : parish.Find("CalibrationPoints");
        stair = markers == null ? null : markers.Find(firstFloorReferences[Mathf.Clamp(referenceIndex, 0, 1)]);
        return parish != null && markers != null && stair != null && parish.Find("FirstFloor_BLOCKOUT") != null;
    }

    private void OnEnable() { SceneView.duringSceneGui += DuringScene; minSize = new Vector2(440, 400); }
    private void OnDisable() { SceneView.duringSceneGui -= DuringScene; SceneView.RepaintAll(); }
    private static Vector3 Heading(Transform t) => Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;

    private void OnGUI()
    {
        EditorGUILayout.LabelField("SLRP AR — Function Hall QR", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Pick the wall immediately LEFT of the middle entrance, as seen from the Multipurpose Hall. " +
            "The marker's blue +Z will copy the selected first-floor stair QR's world yaw. Visitors face the opposite direction, toward the wall. " +
            "Bell Tower and Comfort Room remain selectable destinations.", MessageType.Info);
        if (!TryContext(out Transform parish, out Transform markers, out Transform stair))
        { EditorGUILayout.HelpBox("Open ParishARNavigation in Edit Mode. NavigationMap/ParishMap/CalibrationPoints and the first-floor stair QR must exist.", MessageType.Error); return; }
        if (!initialized)
        {
            floorY = stair.position.y;
            QRMapCalibrator c = FindInScene<QRMapCalibrator>(parish.gameObject.scene);
            if (c != null)
            {
                SerializedProperty h = new SerializedObject(c).FindProperty("qrCenterHeightAboveFloor");
                if (h != null) mountingHeight = h.floatValue;
            }
            Transform existing = markers.Find(MarkerName);
            if (existing != null) floorY = existing.position.y;
            initialized = true;
        }
        EditorGUI.BeginChangeCheck();
        referenceIndex = EditorGUILayout.Popup("Copy facing from", referenceIndex, firstFloorReferences);
        floorY = EditorGUILayout.FloatField("Floor below QR (world Y)", floorY);
        mountingHeight = Mathf.Max(0.05f, EditorGUILayout.FloatField("QR center above floor (m)", mountingHeight));
        standDistance = Mathf.Clamp(EditorGUILayout.FloatField("Standing distance from wall (m)", standDistance), 0.3f, 2.5f);
        if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();
        stair = markers.Find(firstFloorReferences[referenceIndex]);
        if (stair == null) { EditorGUILayout.HelpBox("Chosen stair QR is missing.", MessageType.Error); return; }
        Vector3 forward = Heading(stair);
        if (forward.sqrMagnitude < 0.5f) { EditorGUILayout.HelpBox("Selected stair QR has no valid horizontal forward direction.", MessageType.Error); return; }
        EditorGUILayout.LabelField("Reference world yaw", Quaternion.LookRotation(forward, Vector3.up).eulerAngles.y.ToString("F1") + "°");
        Transform other = markers.Find(firstFloorReferences[1 - referenceIndex]);
        if (other != null && Vector3.Angle(forward, Heading(other)) > 2f)
            EditorGUILayout.HelpBox("The two first-floor stair QR headings differ. Choose the one that faces out from the Function Hall entrance wall.", MessageType.Warning);
        if (GUILayout.Button(picking ? "Cancel wall picking" : "Pick left wall face in Scene view", GUILayout.Height(32)))
        { picking = !picking; status = picking ? "Click the exact wall face where the QR will be pasted (not the door opening). Esc cancels." : "Wall picking canceled."; SceneView.RepaintAll(); }
        EditorGUILayout.HelpBox(status, MessageType.None);
        if (!hasPoint) return;
        EditorGUILayout.LabelField("Selected wall", wallName);
        Vector3 marker = MarkerPosition(forward);
        EditorGUILayout.LabelField("QR floor anchor (world)", marker.ToString("F3"));
        EditorGUILayout.LabelField("QR center (world)", (marker + Vector3.up * mountingHeight).ToString("F3"));
        bool validHeading = Vector3.Dot(forward, wallNormal) >= 0.94f;
        bool validStart = TryStandingPoint(marker, forward, out Vector3 standing);
        if (!validHeading) EditorGUILayout.HelpBox("The copied stair heading does not point out of the clicked face. Choose the other stair reference or the correct wall face. No automatic flip is applied.", MessageType.Error);
        if (!validStart) EditorGUILayout.HelpBox("No nearby first-floor NavMesh point in front of this QR. Check the floor Y, adjust standing distance, or rebake both floors before applying.", MessageType.Error);
        else EditorGUILayout.LabelField("Walkable standing point", standing.ToString("F3"));
        using (new EditorGUI.DisabledScope(!validHeading || !validStart || picking))
            if (GUILayout.Button("Apply five-marker setup (Undo supported)", GUILayout.Height(34)))
                Apply(parish, markers, stair, marker, standing);
        EditorGUILayout.HelpBox("Save the scene after applying. The QR anchor is at FLOOR height, not the QR's mounting height; the measured center height is stored in the calibrator override. No NavMesh rebake is needed for marker-only edits.", MessageType.Info);
    }

    private Vector3 MarkerPosition(Vector3 forward)
    {
        // Actual map marker is the floor directly below the QR center, on the wall face.
        Vector3 p = wallPoint + forward * 0.003f; p.y = floorY; return p;
    }
    private bool TryStandingPoint(Vector3 marker, Vector3 forward, out Vector3 standing)
    {
        Vector3 desired = marker + forward * standDistance;
        standing = desired;
        if (!NavMesh.SamplePosition(desired, out NavMeshHit hit, 0.45f, NavMesh.AllAreas)) return false;
        if (Mathf.Abs(hit.position.y - floorY) > 0.45f || Vector3.Dot(hit.position - marker, forward) < 0.25f) return false;
        standing = hit.position; return true;
    }

    private void DuringScene(SceneView view)
    {
        if (!TryContext(out Transform parish, out Transform markers, out Transform stair)) return;
        Event e = Event.current;
        if (picking)
        {
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { picking = false; e.Use(); Repaint(); return; }
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                Transform first = parish.Find("FirstFloor_BLOCKOUT");
                Physics.SyncTransforms();
                float nearest = float.PositiveInfinity; RaycastHit picked = default; Collider chosen = null;
                foreach (Collider col in first.GetComponentsInChildren<Collider>(false))
                {
                    if (!col.enabled || !IsFunctionHallEntranceWall(col.transform, first)) continue;
                    if (col.Raycast(ray, out RaycastHit hit, 1000f) && hit.distance < nearest)
                    { nearest = hit.distance; picked = hit; chosen = col; }
                }
                if (chosen != null && Mathf.Abs(picked.normal.y) < 0.2f)
                {
                    wallPoint = picked.point; wallNormal = Vector3.ProjectOnPlane(picked.normal, Vector3.up).normalized;
                    wallName = chosen.gameObject.name; hasPoint = true; picking = false;
                    status = "Wall face selected. Check the blue heading and gold visitor position. Apply only if this is the LEFT wall of the middle entrance.";
                }
                else status = "No vertical Function Hall entrance-wall collider hit. Click the face of a south-wall segment, not the floor or doorway.";
                e.Use(); Repaint(); view.Repaint();
            }
        }
        if (!hasPoint || e.type != EventType.Repaint) return;
        Vector3 f = Heading(stair); if (f.sqrMagnitude < 0.5f) return;
        Vector3 anchor = MarkerPosition(f), center = anchor + Vector3.up * mountingHeight;
        Handles.color = Color.cyan; Handles.DrawLine(anchor, center);
        Handles.DrawAAPolyLine(5f, center, center + f * 0.8f);
        Handles.ArrowHandleCap(0, center, Quaternion.LookRotation(f, Vector3.up), 0.8f, EventType.Repaint);
        Handles.Label(center + Vector3.up * 0.18f, "FUNCTION HALL QR / +Z faces visitor");
        if (TryStandingPoint(anchor, f, out Vector3 standing))
        {
            Handles.color = new Color(1f, 0.75f, 0.2f); Handles.SphereHandleCap(0, standing + Vector3.up * 0.08f, Quaternion.identity, 0.22f, EventType.Repaint);
            Handles.Label(standing + Vector3.up * 0.22f, "Visitor standing point / faces wall");
        }
    }

    private static bool IsFunctionHallEntranceWall(Transform t, Transform stop)
    {
        for (; t != null && t != stop; t = t.parent)
            if (t.name.IndexOf("FunctionHall_SouthWall", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private void Apply(Transform parish, Transform markers, Transform stair, Vector3 markerPosition, Vector3 standing)
    {
        Scene scene = parish.gameObject.scene;
        QRMapCalibrator calibrator = FindInScene<QRMapCalibrator>(scene);
        ARTrackedImageManager manager = FindInScene<ARTrackedImageManager>(scene);
        XRReferenceImageLibrary library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(LibraryPath);
        if (calibrator == null || manager == null || !LibraryValid(library))
        { status = "Missing calibrator/tracked-image manager or five-image library; no scene changes applied."; return; }
        SerializedObject cal = new SerializedObject(calibrator);
        SerializedProperty root = cal.FindProperty("calibrationPointsRoot"), heights = cal.FindProperty("markerHeightOverrides");
        SerializedObject tracked = new SerializedObject(manager);
        SerializedProperty trackedLibrary = tracked.FindProperty("m_SerializedLibrary");
        if (root == null || heights == null || trackedLibrary == null)
        { status = "Expected serialized calibration fields were not found; no changes applied."; return; }
        foreach (string s in Required)
            if (s != MarkerName && markers.Find(s) == null) { status = "Missing stair checkpoint: " + s; return; }
        foreach (Transform child in markers)
            if (Array.IndexOf(Required, child.name) < 0 && Array.IndexOf(Retired, child.name) < 0)
            { status = "Unexpected checkpoint " + child.name + "; review it before applying. No changes made."; return; }
        if (!EditorUtility.DisplayDialog("Apply Function Hall QR?", "This removes ONLY the two exterior QR calibration objects and creates/updates the Function Hall QR on " + wallName + ". All eight destinations, stairs, geometry and baked NavMeshes are preserved. Confirm this is the left wall of the middle entrance.", "Apply", "Cancel")) return;
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("SLRP AR five QR checkpoint setup");
        foreach (string retired in Retired)
        {
            Transform old = markers.Find(retired);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
        }
        Transform marker = markers.Find(MarkerName);
        if (marker == null)
        {
            GameObject go = new GameObject(MarkerName); Undo.RegisterCreatedObjectUndo(go, "Create Function Hall QR");
            Undo.SetTransformParent(go.transform, markers, "Parent Function Hall QR"); marker = go.transform;
        }
        Undo.RecordObject(marker, "Place Function Hall QR"); Undo.RecordObject(marker.gameObject, "Set marker layer");
        marker.gameObject.layer = stair.gameObject.layer;
        marker.gameObject.SetActive(true);
        marker.SetPositionAndRotation(markerPosition, Quaternion.LookRotation(Heading(stair), Vector3.up));
        marker.localScale = Vector3.one;
        Transform start = marker.Find("NavigationStart");
        if (start == null)
        {
            GameObject go = new GameObject("NavigationStart"); Undo.RegisterCreatedObjectUndo(go, "Create QR standing point");
            Undo.SetTransformParent(go.transform, marker, "Parent QR standing point"); start = go.transform;
        }
        Undo.RecordObject(start, "Position QR standing point"); start.position = standing; start.gameObject.layer = marker.gameObject.layer; start.gameObject.SetActive(true);
        Undo.RecordObject(calibrator, "Update calibration height overrides");
        root.objectReferenceValue = markers;
        for (int i = heights.arraySize - 1; i >= 0; i--)
        {
            string n = heights.GetArrayElementAtIndex(i).FindPropertyRelative("imageName").stringValue;
            if (n == MarkerName || Array.IndexOf(Retired, n) >= 0) heights.DeleteArrayElementAtIndex(i);
        }
        int idx = heights.arraySize; heights.InsertArrayElementAtIndex(idx);
        SerializedProperty entry = heights.GetArrayElementAtIndex(idx);
        entry.FindPropertyRelative("imageName").stringValue = MarkerName;
        entry.FindPropertyRelative("qrCenterHeightMeters").floatValue = mountingHeight;
        cal.ApplyModifiedProperties();
        Undo.RecordObject(manager, "Assign parish reference library"); trackedLibrary.objectReferenceValue = library; tracked.ApplyModifiedProperties();
        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = marker.gameObject;
        status = "Applied! Save the scene (Cmd+S), validate the five checkpoints, and preview Function Hall routes before rebuilding.";
        Debug.Log("[SLRP AR] Five-marker scene setup applied. " + MarkerName + " at " + marker.position.ToString("F3") + ", yaw " + marker.eulerAngles.y.ToString("F1") + ", QR center height " + mountingHeight.ToString("F2") + " m. Exterior destinations preserved.");
        SceneView.RepaintAll();
    }

    private static T FindInScene<T>(Scene scene) where T : Component
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        { T found = root.GetComponentInChildren<T>(true); if (found != null) return found; }
        return null;
    }
    internal static bool LibraryValid(XRReferenceImageLibrary library)
    {
        if (library == null || library.count != Required.Length) return false;
        HashSet<string> found = new HashSet<string>();
        for (int i = 0; i < library.count; i++) found.Add(library[i].name);
        return found.SetEquals(Required);
    }
}

// Fail a parish player build if the explicit scene migration has not been applied.
// Does not touch other scenes or modify scene contents during a build.
public sealed class SLRPParishQRBuildCheck : IProcessSceneWithReport
{
    public int callbackOrder => 0;
    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (report == null || scene.path != "Assets/Scenes/ParishARNavigation.unity") return;
        Transform parish = SLRPFunctionHallQRPlacement.FindParish(scene);
        Transform markers = parish == null ? null : parish.Find("CalibrationPoints");
        if (markers == null || markers.childCount != SLRPFunctionHallQRPlacement.Required.Length)
            throw new BuildFailedException("SLRP AR: Run Tools > SLRP AR > 10 - Place Function Hall QR, then save the parish scene before building.");
        foreach (string id in SLRPFunctionHallQRPlacement.Required)
            if (markers.Find(id) == null || !markers.Find(id).gameObject.activeInHierarchy)
                throw new BuildFailedException("SLRP AR: Missing or inactive checkpoint " + id + ". Apply the Function Hall QR placement and save.");
        if (!SLRPFunctionHallQRPlacement.LibraryValid(AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(SLRPFunctionHallQRPlacement.LibraryPath)))
            throw new BuildFailedException("SLRP AR: Parish reference library must contain the four stairs and Function Hall QR only.");
    }
}
#endif
