using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// One camera-following horizontal guidance arrow, like the referenced project.
/// The arrow's LOCATION follows the mapped phone position; its DIRECTION points
/// toward the next NavMesh waypoint. It never reads the magnetic compass.
/// The separate IndoorPathVisualizer handles route/arrival and disables the
/// old arrow trail in this mode.
/// </summary>
public class WayfinderFollowingArrow : MonoBehaviour
{
    [Header("Existing navigation references (auto-found if empty)")]
    [SerializeField] private IndoorPathVisualizer navigator;
    [SerializeField] private QRMapCalibrator qrCalibrator;
    [SerializeField] private Transform arMapOrigin;
    [SerializeField] private Transform arCamera;

    [Header("When to show the arrow")]
    [Tooltip("Match the route preparation delay displayed by your UI.")]
    [SerializeField, Min(0f)] private float routePreparationSeconds = 2.5f;

    [Header("Arrow on the navigation floor")]
    [Tooltip("How far ahead of the phone (horizontal XZ plane) to position the single guidance arrow, in metres.")]
    [SerializeField, Min(0.1f)] private float aheadDistance = 0.75f;
    [Tooltip("How far ahead along the path to look when deciding the arrow direction.")]
    [SerializeField, Min(0.2f)] private float waypointLookAhead = 1.6f;
    [Tooltip("Height ABOVE the virtual NavMesh floor (not the real-world floor calibration).")]
    [SerializeField] private float floorOffset = 0.06f;
    [SerializeField, Range(0.4f, 2f)] private float arrowSize = 1f;

    [Header("Smoothness")]
    [SerializeField, Min(0.05f)] private float routeUpdateSeconds = 0.2f;
    [SerializeField, Min(0.1f)] private float positionSmoothness = 9f;
    [SerializeField, Min(0.1f)] private float rotationSmoothness = 11f;
    [SerializeField, Min(0.2f)] private float navMeshSearchRadius = 2f;

    private NavMeshPath calculatedPath;
    private Transform arrowTransform;
    private GameObject arrowObject;
    private Mesh arrowMesh;
    private Material outlineMaterial;
    private Material faceMaterial;

    private Transform lastDestination;
    private float destinationSelectedAt;
    private float nextRouteUpdate;
    private Vector3 desiredMapPosition;
    private Quaternion desiredMapRotation;
    private bool hasRoute;
    private bool wasVisible;

    private void Start()
    {
        // Find the existing components. No scene or script replacements needed.
        if (navigator == null)
            navigator = FindFirstObjectByType<IndoorPathVisualizer>();
        if (qrCalibrator == null)
            qrCalibrator = FindFirstObjectByType<QRMapCalibrator>();
        if (arCamera == null && Camera.main != null)
            arCamera = Camera.main.transform;
        if (arMapOrigin == null)
        {
            GameObject mapObject = GameObject.Find("ARMapOrigin");
            if (mapObject != null)
                arMapOrigin = mapObject.transform;
        }

        if (navigator == null || qrCalibrator == null ||
            arMapOrigin == null || arCamera == null)
        {
            Debug.LogError("SLRP AR: Could not find Navigator, QR Calibrator, ARMapOrigin, or AR Camera.");
            enabled = false;
            return;
        }

        calculatedPath = new NavMeshPath();
        BuildCrimsonChevron();
        SetArrowVisible(false);
    }

    private void Update()
    {
        if (navigator == null || qrCalibrator == null ||
            arCamera == null || arMapOrigin == null)
            return;

        Transform destination = navigator.CurrentDestination;

        // Always require a QR scan AND a selected destination.
        if (!qrCalibrator.IsCalibrated || destination == null || navigator.HasArrived)
        {
            lastDestination = null;
            hasRoute = false;
            SetArrowVisible(false);
            return;
        }

        // When a NEW destination is chosen, hide the arrow while the UI
        // displays "Preparing your route...".
        if (destination != lastDestination)
        {
            lastDestination = destination;
            destinationSelectedAt = Time.time;
            nextRouteUpdate = 0f;
            hasRoute = false;
            SetArrowVisible(false);
        }

        if (Time.time - destinationSelectedAt < routePreparationSeconds)
            return;

        // We only calculate the NavMesh route a few times per second.
        // Between calculations, the visual smoothly moves to its new place.
        if (Time.time >= nextRouteUpdate)
        {
            nextRouteUpdate = Time.time + routeUpdateSeconds;
            hasRoute = UpdateRoute(destination);
            if (!hasRoute)
                SetArrowVisible(false);
        }

        if (!hasRoute || arrowTransform == null)
            return;

        if (!wasVisible)
        {
            arrowTransform.localPosition = desiredMapPosition;
            arrowTransform.localRotation = desiredMapRotation;
        }
        else
        {
            float posT = 1f - Mathf.Exp(-positionSmoothness * Time.deltaTime);
            float rotT = 1f - Mathf.Exp(-rotationSmoothness * Time.deltaTime);
            arrowTransform.localPosition = Vector3.Lerp(
                arrowTransform.localPosition, desiredMapPosition, posT);
            arrowTransform.localRotation = Quaternion.Slerp(
                arrowTransform.localRotation, desiredMapRotation, rotT);
        }

        SetArrowVisible(true);
    }

    private bool UpdateRoute(Transform destination)
    {
        // NavMesh coordinates belong to the FIXED house map.
        // Convert the AR camera into that coordinate system first.
        Vector3 cameraInMap = arMapOrigin.InverseTransformPoint(arCamera.position);

        NavMeshHit startHit;
        NavMeshHit endHit;
        if (!NavMesh.SamplePosition(cameraInMap, out startHit,
                navMeshSearchRadius, NavMesh.AllAreas))
            return false;
        if (!NavMesh.SamplePosition(destination.position, out endHit,
                navMeshSearchRadius, NavMesh.AllAreas))
            return false;
        if (!NavMesh.CalculatePath(startHit.position, endHit.position,
                NavMesh.AllAreas, calculatedPath))
            return false;
        if (calculatedPath.status != NavMeshPathStatus.PathComplete ||
            calculatedPath.corners.Length < 2)
            return false;

        // Reference-project behavior: follow the USER horizontally, not the
        // fixed Room3 marker. Put the arrow near/in front of the phone so it
        // remains visible, but aim it at the upcoming WALKABLE path waypoint.
        // Convert camera.forward to map space: it is NOT a magnetic compass.
        Vector3 facingInMap = arMapOrigin.InverseTransformDirection(arCamera.forward);
        facingInMap.y = 0f;
        if (facingInMap.sqrMagnitude < 0.01f)
            facingInMap = Vector3.forward;
        facingInMap.Normalize();

        Vector3 arrowPoint = cameraInMap + facingInMap * aheadDistance;
        // Keep the indicator near navigable space if a phone faces a wall.
        // Don't snap it to a distant corridor across the wall.
        NavMeshHit arrowHit;
        if (NavMesh.SamplePosition(arrowPoint, out arrowHit, 0.35f, NavMesh.AllAreas))
            arrowPoint = arrowHit.position;
        arrowPoint.y = startHit.position.y + floorOffset;

        Vector3 lookPoint = PointAlongPath(calculatedPath, waypointLookAhead);
        Vector3 routeDirection = lookPoint - startHit.position;
        routeDirection.y = 0f;
        if (routeDirection.sqrMagnitude < 0.0001f)
        {
            Vector3[] corners = calculatedPath.corners;
            routeDirection = corners[corners.Length - 1] -
                             corners[corners.Length - 2];
            routeDirection.y = 0f;
        }
        if (routeDirection.sqrMagnitude < 0.0001f)
            return false;

        desiredMapPosition = arrowPoint;
        desiredMapRotation = Quaternion.LookRotation(
            routeDirection.normalized, Vector3.up);
        return true;
    }

    // Walk along the calculated NavMesh corners by the given distance.
    private static Vector3 PointAlongPath(NavMeshPath path, float metres)
    {
        Vector3[] corners = path.corners;
        float leftToWalk = Mathf.Max(0f, metres);

        for (int i = 0; i < corners.Length - 1; i++)
        {
            float segmentLength = Vector3.Distance(corners[i], corners[i + 1]);
            if (segmentLength < 0.0001f)
                continue;

            if (leftToWalk <= segmentLength)
                return Vector3.Lerp(corners[i], corners[i + 1], leftToWalk / segmentLength);

            leftToWalk -= segmentLength;
        }
        return corners[corners.Length - 1];
    }

    private void BuildCrimsonChevron()
    {
        // Runtime-only geometry: no prefab, material, or .meta dependencies.
        arrowObject = new GameObject("SLRP_AR_Following_Crimson_Arrow (Runtime)");
        arrowTransform = arrowObject.transform;
        arrowTransform.SetParent(arMapOrigin, false);
        arrowTransform.localScale = Vector3.one * arrowSize;

        // The arrow points along LOCAL +Z and lies flat on the XZ plane.
        arrowMesh = new Mesh { name = "SLRP AR Chevron" };
        arrowMesh.vertices = new Vector3[]
        {
            new Vector3( 0.00f, 0f,  0.39f), // arrow tip
            new Vector3( 0.34f, 0f, -0.06f), // outside right
            new Vector3( 0.22f, 0f, -0.19f), // inside right
            new Vector3( 0.00f, 0f,  0.09f), // inner V
            new Vector3(-0.22f, 0f, -0.19f), // inside left
            new Vector3(-0.34f, 0f, -0.06f)  // outside left
        };
        arrowMesh.triangles = new int[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5 };
        arrowMesh.RecalculateNormals();
        arrowMesh.RecalculateBounds();

        // Use the project's URP unlit shader if available.
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            Debug.LogError("SLRP AR: No compatible unlit shader found.");
            return;
        }

        outlineMaterial = MakeMaterial(shader, new Color(0.98f, 0.76f, 0.36f, 1f));
        faceMaterial = MakeMaterial(shader, new Color(0.67f, 0.09f, 0.15f, 1f));

        // Gold border underneath + crimson inner chevron above it.
        CreateChevronLayer("Warm Gold Outline", outlineMaterial,
            new Vector3(1.11f, 1f, 1.11f), 0.002f);
        CreateChevronLayer("Crimson Face", faceMaterial,
            Vector3.one, 0.009f);
    }

    private static Material MakeMaterial(Shader shader, Color tint)
    {
        Material material = new Material(shader);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
        if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
        return material;
    }

    private void CreateChevronLayer(string layerName, Material material,
        Vector3 layerScale, float height)
    {
        GameObject layer = new GameObject(layerName);
        layer.transform.SetParent(arrowTransform, false);
        layer.transform.localPosition = new Vector3(0f, height, 0f);
        layer.transform.localScale = layerScale;
        layer.AddComponent<MeshFilter>().sharedMesh = arrowMesh;
        MeshRenderer renderer = layer.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private void SetArrowVisible(bool visible)
    {
        if (arrowObject != null && arrowObject.activeSelf != visible)
            arrowObject.SetActive(visible);
        wasVisible = visible;
    }

    private void OnDisable()
    {
        SetArrowVisible(false);
    }

    private void OnDestroy()
    {
        if (arrowObject != null) Destroy(arrowObject);
        if (arrowMesh != null) Destroy(arrowMesh);
        if (outlineMaterial != null) Destroy(outlineMaterial);
        if (faceMaterial != null) Destroy(faceMaterial);
    }
}
