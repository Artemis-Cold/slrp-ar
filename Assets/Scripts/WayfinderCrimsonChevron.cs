using UnityEngine;
using UnityEngine.Rendering;

// OPTIONAL visual-only prefab component. Does not read camera heading,
// set world positions, modify QR tracking, or influence the NavMesh route.
// The existing IndoorPathVisualizer sets the prefab's world-local pose.
[DisallowMultipleComponent]
public sealed class WayfinderCrimsonChevron : MonoBehaviour
{
    [Header("Flat AR arrow colours")]
    [SerializeField] private Material crimsonMaterial;
    [SerializeField] private Material outlineMaterial;

    private static Mesh cachedChevron;

    private void Awake()
    {
        // The original box pieces remain visible if materials are missing.
        if (crimsonMaterial == null || outlineMaterial == null)
        {
            Debug.LogWarning("SLRP AR crimson arrow materials are not assigned.", this);
            return;
        }

        MeshFilter filter = GetComponent<MeshFilter>();
        if (filter == null) filter = gameObject.AddComponent<MeshFilter>();
        MeshRenderer renderer = GetComponent<MeshRenderer>();
        if (renderer == null) renderer = gameObject.AddComponent<MeshRenderer>();

        filter.sharedMesh = GetMesh();
        // First submesh is the pale outline; second is the crimson face.
        renderer.sharedMaterials = new Material[] { outlineMaterial, crimsonMaterial };
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

        // Hide the fallback cubes ONLY after the flat mesh is ready.
        foreach (MeshRenderer child in GetComponentsInChildren<MeshRenderer>(true))
        {
            if (child != renderer) child.enabled = false;
        }
    }

    private static Mesh GetMesh()
    {
        if (cachedChevron != null) return cachedChevron;

        // Unit scale roughly 0.50m wide by 0.72m long. Local +Z points
        // forward: this matches Quaternion.LookRotation in the navigator.
        // The arrow is flat on the local XZ (floor) plane.
        Vector3[] verts =
        {
            // Thin ivory outline underneath
            new Vector3(0.00f, 0.000f, 0.43f),   // tip
            new Vector3(0.25f, 0.000f, -0.01f),  // right wing
            new Vector3(0.16f, 0.000f, -0.28f),  // right tail
            new Vector3(0.00f, 0.000f, -0.12f),  // back notch
            new Vector3(-0.16f, 0.000f, -0.28f), // left tail
            new Vector3(-0.25f, 0.000f, -0.01f), // left wing
            // Crimson face sits a few millimetres above the outline
            new Vector3(0.00f, 0.004f, 0.38f),
            new Vector3(0.205f, 0.004f, -0.018f),
            new Vector3(0.133f, 0.004f, -0.23f),
            new Vector3(0.00f, 0.004f, -0.085f),
            new Vector3(-0.133f, 0.004f, -0.23f),
            new Vector3(-0.205f, 0.004f, -0.018f)
        };

        Mesh mesh = new Mesh { name = "SLRP_AR_Flat_Crimson_Chevron" };
        mesh.vertices = verts;
        mesh.uv = new Vector2[]
        {
            new Vector2(.5f,1f), new Vector2(1f,.4f), new Vector2(.8f,0f),
            new Vector2(.5f,.25f), new Vector2(.2f,0f), new Vector2(0f,.4f),
            new Vector2(.5f,1f), new Vector2(1f,.4f), new Vector2(.8f,0f),
            new Vector2(.5f,.25f), new Vector2(.2f,0f), new Vector2(0f,.4f)
        };
        mesh.subMeshCount = 2;
        mesh.SetTriangles(new int[]
        {
            0,1,2, 0,2,3, 0,3,4, 0,4,5
        }, 0);
        mesh.SetTriangles(new int[]
        {
            6,7,8, 6,8,9, 6,9,10, 6,10,11
        }, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        cachedChevron = mesh;
        return cachedChevron;
    }
}
