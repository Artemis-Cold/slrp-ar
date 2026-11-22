#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Visualizes real baked-NavMesh route corners and floor-flat crimson chevrons
/// inside Scene View. This is Editor-only and creates NO serialized objects.
/// For cross-floor journeys, the two separate paths are drawn on their own
/// floors; a QR handoff is shown instead of a fake 3D path through the stairs.
/// </summary>
public sealed class SLRPParishRouteArrowPreview : EditorWindow
{
    private static readonly string[] Starts =
    {
        "F1_LeftStairs_QR", "F1_RightStairs_QR",
        "F1_FunctionHall_QR",
        "F2_LeftStairs_QR", "F2_RightStairs_QR"
    };
    private static readonly string[] Goals =
    {
        "F1_FunctionHall", "F1_MultipurposeHall", "F1_ParishOffice", "F1_ParishStore",
        "F1_BellTower", "F1_ComfortRoom", "F2_MainChurch", "F2_Altar"
    };
    private static readonly string[] StairOptions = { "Automatic (shortest valid)", "Left", "Right" };

    private sealed class RoutePart
    {
        public string name;
        public Vector3[] corners;
        public float length;
    }

    private int startIndex;
    private int goalIndex;
    private int stairChoice;
    private float sampleRadius = 1.2f;
    private float maxFloorError = 0.65f;
    private bool showPreview;
    private string report = "Choose a start QR and destination, then click 'Preview route in Scene'.";
    private readonly List<RoutePart> parts = new List<RoutePart>();
    private float arrowSpacing = 0.7f;
    private float firstArrowDistance = 0.55f;
    private float arrowScale = 1f;
    private int maximumArrows = 45;
    private float arrowEndClearance = 0.55f;
    private bool showFullRouteInEditor = true;
    private const int EditorSafetyArrowLimit = 350;

    [MenuItem("Tools/SLRP AR/9 - Scene Route Arrow Preview")]
    private static void Open()
    {
        SLRPParishRouteArrowPreview window = GetWindow<SLRPParishRouteArrowPreview>("SLRP AR Route Preview");
        window.minSize = new Vector2(390, 330);
        window.Show();
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += DrawScenePreview;
        Undo.undoRedoPerformed += OnGeometryChanged;
        EditorApplication.hierarchyChanged += OnGeometryChanged;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= DrawScenePreview;
        Undo.undoRedoPerformed -= OnGeometryChanged;
        EditorApplication.hierarchyChanged -= OnGeometryChanged;
        parts.Clear();
        SceneView.RepaintAll();
    }

    private void OnGeometryChanged()
    {
        if (showPreview) Recalculate(false);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("SLRP AR • Parish Route Arrows", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Preview floor-flat crimson arrows following ACTUAL baked NavMesh paths, " +
            "not a straight line between locations. No QR scan or Play Mode needed. " +
            "This does not alter your scene, markers, or baked surfaces.", MessageType.Info);
        EditorGUI.BeginChangeCheck();
        startIndex = Mathf.Clamp(startIndex, 0, Starts.Length - 1);
        startIndex = EditorGUILayout.Popup("Start QR", startIndex, Starts);
        goalIndex = EditorGUILayout.Popup("Destination", goalIndex, Goals);
        stairChoice = EditorGUILayout.Popup("Cross-floor stair", stairChoice, StairOptions);
        sampleRadius = EditorGUILayout.Slider("NavMesh sample radius (m)", sampleRadius, 0.25f, 2f);
        showFullRouteInEditor = EditorGUILayout.Toggle("Show full route (Editor only)", showFullRouteInEditor);
        if (EditorGUI.EndChangeCheck() && showPreview) Recalculate(false);
        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Preview route in Scene", GUILayout.Height(30)))
            {
                showPreview = true;
                Recalculate(true);
            }
            if (GUILayout.Button("Clear preview", GUILayout.Height(30)))
            {
                showPreview = false;
                parts.Clear();
                report = "Preview cleared. Your scene objects were never changed.";
                SceneView.RepaintAll();
            }
        }
        if (GUILayout.Button("Recalculate after rebaking"))
        {
            showPreview = true;
            Recalculate(true);
        }
        EditorGUILayout.HelpBox(report, parts.Count > 0 ? MessageType.Info : MessageType.Warning);
        EditorGUILayout.HelpBox(
            "On-device AR displays at most " + maximumArrows + " chevrons ahead (roughly " +
            (firstArrowDistance + arrowSpacing * (maximumArrows - 1)).ToString("F1") +
            " m). It recalculates the trail as you walk. 'Show full route' is Editor-only. " +
            "If the YELLOW path ends early, investigate NavMesh connectivity; " +
            "if only red arrows end early, check the arrow limit. " +
            "Optional destination child 'NavigationArrival' stops at an entrance; " +
            "optional QR child 'NavigationStart' sets the public standing point for Scene previews.",
            MessageType.None);
        EditorGUILayout.HelpBox(
            "The start point is the QR's X/Z projected to its floor, not a live tracked camera position. " +
            "The drawn chevrons approximate the in-app arrow placement; alignment and tracking " +
            "still need testing at the physical parish.", MessageType.None);
    }

    private void ReadArrowSettings()
    {
        IndoorPathVisualizer visualizer = FindFirstObjectByType<IndoorPathVisualizer>();
        if (visualizer == null) return;
        SerializedObject serialized = new SerializedObject(visualizer);
        SerializedProperty spacing = serialized.FindProperty("arrowSpacing");
        SerializedProperty first = serialized.FindProperty("firstArrowDistance");
        SerializedProperty scale = serialized.FindProperty("arrowScale");
        SerializedProperty limit = serialized.FindProperty("maximumVisibleArrows");
        SerializedProperty endClearance = serialized.FindProperty("arrowEndClearance");
        if (spacing != null) arrowSpacing = Mathf.Max(0.2f, spacing.floatValue);
        if (first != null) firstArrowDistance = Mathf.Max(0.05f, first.floatValue);
        if (scale != null) arrowScale = Mathf.Max(0.1f, scale.floatValue);
        if (limit != null) maximumArrows = Mathf.Max(1, limit.intValue);
        if (endClearance != null) arrowEndClearance = Mathf.Max(0f, endClearance.floatValue);
    }

    private static string Floor(string name) => name.StartsWith("F1_", StringComparison.Ordinal) ? "F1" :
        name.StartsWith("F2_", StringComparison.Ordinal) ? "F2" : "";

    private void Recalculate(bool log)
    {
        parts.Clear();
        if (Application.isPlaying || SceneManager.GetActiveScene().name != "ParishARNavigation")
        {
            report = "Open ParishARNavigation in Edit Mode to view the parish NavMesh preview.";
            Repaint();
            return;
        }
        GameObject map = GameObject.Find("NavigationMap");
        Transform parish = map == null ? null : map.transform.Find("ParishMap");
        if (parish == null)
        {
            report = "Missing NavigationMap/ParishMap. Current hierarchy cannot be previewed.";
            Repaint();
            return;
        }
        ReadArrowSettings();
        Transform markers = parish.Find("CalibrationPoints");
        Transform destinations = parish.Find("ParishDestinations");
        Transform stairs = parish.Find("StairTransitions");
        if (markers == null || destinations == null || stairs == null)
        {
            report = "Missing CalibrationPoints, ParishDestinations, or StairTransitions hierarchy.";
            Repaint();
            return;
        }
        startIndex = Mathf.Clamp(startIndex, 0, Starts.Length - 1);
        Transform start = markers.Find(Starts[startIndex]);
        Transform target = destinations.Find(Goals[goalIndex]);
        if (start == null || target == null)
        {
            report = "Missing current scene Transform: " + (start == null ? Starts[startIndex] : Goals[goalIndex]) +
                ". Check whether locations were renamed or moved.";
            Repaint();
            return;
        }

        Vector3 targetArrival = IndoorPathVisualizer.GetNavTargetPosition(target);
        Vector3 startingPosition = IndoorPathVisualizer.GetNavStartPosition(start);
        if (Floor(start.name) == Floor(target.name))
        {
            if (TryPath(startingPosition, targetArrival, out RoutePart same, out string issue))
            {
                same.name = "Direct route " + start.name + " → " + target.name;
                parts.Add(same);
                report = "PASS: " + same.name + " | " + same.length.ToString("F1") +
                    " m | " + same.corners.Length + " path corners. Crimson arrows are visible in Scene View.";
            }
            else report = "ROUTE FAILED: " + issue;
        }
        else
        {
            float best = float.PositiveInfinity;
            RoutePart firstPart = null, secondPart = null;
            string chosenSide = "";
            string leftReason = "", rightReason = "";
            for (int side = 0; side < 2; side++)
            {
                if (stairChoice == 1 && side != 0 || stairChoice == 2 && side != 1) continue;
                bool left = side == 0;
                string name = left ? "LeftStairs" : "RightStairs";
                bool fromFirstFloor = Floor(start.name) == "F1";
                string currentName = (fromFirstFloor ? "F1_" : "F2_") + name +
                    (fromFirstFloor ? "_Approach" : "_Exit");
                string otherName = (fromFirstFloor ? "F2_" : "F1_") + name +
                    (fromFirstFloor ? "_Exit" : "_Approach");
                Transform current = stairs.Find(currentName);
                Transform other = stairs.Find(otherName);
                if (current == null || other == null)
                {
                    if (left) leftReason = "Missing " + (current == null ? currentName : otherName);
                    else rightReason = "Missing " + (current == null ? currentName : otherName);
                    continue;
                }
                bool validA = TryPath(startingPosition, current.position, out RoutePart toStairs, out string whyA);
                bool validB = TryPath(other.position, targetArrival, out RoutePart fromStairs, out string whyB);
                if (!validA || !validB)
                {
                    string why = !validA ? "current floor: " + whyA : "target floor: " + whyB;
                    if (left) leftReason = why;
                    else rightReason = why;
                    continue;
                }
                float total = toStairs.length + fromStairs.length;
                if (total < best)
                {
                    best = total;
                    chosenSide = left ? "Left" : "Right";
                    firstPart = toStairs;
                    secondPart = fromStairs;
                }
            }
            if (firstPart != null)
            {
                firstPart.name = "Current floor → " + chosenSide + " Stairs (STOP + scan next floor QR)";
                secondPart.name = "New floor " + chosenSide + " Stairs → " + target.name;
                parts.Add(firstPart);
                parts.Add(secondPart);
                report = "PASS: " + chosenSide + " public stairs | " + best.ToString("F1") +
                    " m of separate floor paths. Two arrow trails are shown; no arrows through stairwell. " +
                    "At the stairs, pause navigation and scan that floor's QR.";
            }
            else report = "CROSS-FLOOR ROUTE FAILED. Left: " +
                (leftReason == "" ? "not selected" : leftReason) + "; Right: " +
                (rightReason == "" ? "not selected" : rightReason) +
                ". Check both surfaces and staircase waypoints without moving calibrated positions blindly.";
        }
        if (log)
        {
            if (parts.Count > 0) Debug.Log("[SLRP AR route preview] " + report);
            else Debug.LogWarning("[SLRP AR route preview] " + report);
        }
        Repaint();
        SceneView.RepaintAll();
    }

    private bool TryPath(Vector3 from, Vector3 goal, out RoutePart part, out string issue)
    {
        part = null;
        issue = "";
        // Same floor approach: QR markers can be mounted above the walking surface.
        // Project start Y to the known waypoint/destination floor, as runtime does.
        from.y = goal.y;
        if (!NavMesh.SamplePosition(from, out NavMeshHit a, sampleRadius, NavMesh.AllAreas) ||
            Mathf.Abs(a.position.y - goal.y) > maxFloorError)
        {
            issue = "start outside this floor's baked NavMesh (check QR X/Z and surface geometry)";
            return false;
        }
        if (!NavMesh.SamplePosition(goal, out NavMeshHit b, sampleRadius, NavMesh.AllAreas) ||
            Mathf.Abs(b.position.y - goal.y) > maxFloorError)
        {
            issue = "destination/stair waypoint outside this floor's baked NavMesh";
            return false;
        }
        NavMeshPath path = new NavMeshPath();
        if (!NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) ||
            path.status != NavMeshPathStatus.PathComplete)
        {
            issue = "no complete path (" + path.status + ")";
            return false;
        }
        foreach (Vector3 corner in path.corners)
            if (Mathf.Abs(corner.y - b.position.y) > maxFloorError)
            {
                issue = "route crosses wrong floor or stairwell";
                return false;
            }
        float length = 0f;
        for (int i = 1; i < path.corners.Length; i++)
            length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        part = new RoutePart { corners = path.corners, length = length };
        return true;
    }

    private void DrawScenePreview(SceneView sceneView)
    {
        if (!showPreview || parts.Count == 0) return;
        // Draw on the current baked floor, not against a live AR camera transform.
        CompareFunction oldZTest = Handles.zTest;
        Handles.zTest = CompareFunction.LessEqual;
        try
        {
            foreach (RoutePart part in parts)
            {
                Vector3[] points = part.corners;
                if (points == null || points.Length == 0) continue;
                Handles.color = new Color(1f, 0.78f, 0.40f, 0.95f);
                Vector3[] raised = new Vector3[points.Length];
                for (int i = 0; i < points.Length; i++) raised[i] = points[i] + Vector3.up * 0.04f;
                if (raised.Length > 1) Handles.DrawAAPolyLine(4f, raised);
                DrawChevrons(points);
                Handles.color = Color.white;
                Handles.Label(points[0] + Vector3.up * 0.25f, part.name);
                Handles.Label(points[points.Length - 1] + Vector3.up * 0.25f,
                    part.name.Contains("STOP") ? "STOP: rescan upstairs/downstairs QR" : "DESTINATION / ROUTE END");
            }
        }
        finally
        {
            Handles.zTest = oldZTest;
        }
    }

    private void DrawChevrons(Vector3[] corners)
    {
        float traversal = 0f;
        float next = firstArrowDistance;
        int used = 0;
        int arrowLimit = showFullRouteInEditor ? EditorSafetyArrowLimit : maximumArrows;
        float routeLength = 0f;
        for (int i = 1; i < corners.Length; i++)
        {
            Vector3 edge = corners[i] - corners[i - 1];
            edge.y = 0f;
            routeLength += edge.magnitude;
        }
        float lastArrow = Mathf.Max(0f, routeLength - arrowEndClearance);
        for (int i = 1; i < corners.Length && used < arrowLimit; i++)
        {
            Vector3 from = corners[i - 1], to = corners[i];
            Vector3 direction = to - from;
            direction.y = 0f;
            float length = direction.magnitude;
            if (length < 0.02f) continue;
            direction /= length;
            while (next <= traversal + length && next <= lastArrow && used < arrowLimit)
            {
                float fraction = Mathf.Clamp01((next - traversal) / length);
                Vector3 position = Vector3.Lerp(from, to, fraction) + Vector3.up * 0.07f;
                DrawChevron(position, Quaternion.LookRotation(direction, Vector3.up), arrowScale);
                next += arrowSpacing;
                used++;
            }
            traversal += length;
        }
    }

    private static void DrawChevron(Vector3 pos, Quaternion rotation, float scale)
    {
        // Match the flat +Z chevron shape used by the real SLRP AR arrow mesh.
        Vector3[] outline =
        {
            new Vector3(0f, 0f, 0.43f), new Vector3(.25f, 0f, -.01f),
            new Vector3(.16f, 0f, -.28f), new Vector3(0f, 0f, -.12f),
            new Vector3(-.16f, 0f, -.28f), new Vector3(-.25f, 0f, -.01f)
        };
        DrawTriangles(pos, rotation, scale, outline, new Color(1f, 0.90f, 0.68f));
        Vector3[] face =
        {
            new Vector3(0f, .004f, .38f), new Vector3(.205f, .004f, -.018f),
            new Vector3(.133f, .004f, -.23f), new Vector3(0f, .004f, -.085f),
            new Vector3(-.133f, .004f, -.23f), new Vector3(-.205f, .004f, -.018f)
        };
        DrawTriangles(pos, rotation, scale, face, new Color(.65f, .07f, .12f));
    }

    private static void DrawTriangles(Vector3 pos, Quaternion rotation, float scale,
        Vector3[] verts, Color color)
    {
        Handles.color = color;
        // Triangle fan: the same indices used by WayfinderCrimsonChevron's generated mesh.
        for (int i = 1; i < 5; i++)
        {
            Vector3 a = pos + rotation * (verts[0] * scale);
            Vector3 b = pos + rotation * (verts[i] * scale);
            Vector3 c = pos + rotation * (verts[i + 1] * scale);
            Handles.DrawAAConvexPolygon(a, b, c);
        }
    }
}
#endif
