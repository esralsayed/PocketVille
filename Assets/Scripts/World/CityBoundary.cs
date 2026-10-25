using UnityEngine;

/// <summary>
/// Invisible walls around the drivable part of the city, sized automatically from the roads.
/// Four BoxColliders (West / East / South / North) are created as children of this object.
///
/// - Add this to ONE empty object. Right-click the component header > "Rebuild Walls"
///   (or call Rebuild()) after you extend the city.
/// - With "Rebuild On Play" ticked (default) the walls are also re-fitted every time the game starts,
///   so they always match the roads even if you forgot to rebuild.
/// - The walls stop anything that has a collider: the rickshaw, future vehicles and the player on foot.
/// - They are drawn as red boxes in the Scene view (Gizmos) and have no renderer, so they are invisible in game.
/// </summary>
public class CityBoundary : MonoBehaviour
{
    [Tooltip("Objects (by name) whose renderers define the drivable area. Add more names if the city grows.")]
    [SerializeField] string[] areaRootNames = { "Roads & Infrastructure" };
    [Tooltip("How far outside the roads the walls stand (meters).")]
    [SerializeField] float margin = 10f;
    [Tooltip("Wall height (meters), measured up from just below the lowest road.")]
    [SerializeField] float wallHeight = 30f;
    [Tooltip("Wall thickness (meters). Keep at least 2 so fast vehicles cannot pass through.")]
    [SerializeField] float wallThickness = 2f;
    [Tooltip("Re-fit the walls to the roads every time the game starts.")]
    [SerializeField] bool rebuildOnPlay = true;

    const string WallPrefix = "Wall_";

    void Awake()
    {
        if (rebuildOnPlay)
            Rebuild();
    }

    [ContextMenu("Rebuild Walls")]
    public void Rebuild()
    {
        Bounds area;
        if (!TryGetAreaBounds(out area))
        {
            Debug.LogWarning("CityBoundary: none of the area objects were found, no walls were built.");
            return;
        }

        ClearWalls();

        float minX = area.min.x - margin, maxX = area.max.x + margin;
        float minZ = area.min.z - margin, maxZ = area.max.z + margin;
        float baseY = area.min.y - 5f;
        float centerY = baseY + wallHeight * 0.5f;
        float width = maxX - minX, depth = maxZ - minZ;
        float t = Mathf.Max(0.5f, wallThickness);

        // walls overlap at the corners (+t) so there are no gaps
        MakeWall("West",  new Vector3(minX - t * 0.5f, centerY, (minZ + maxZ) * 0.5f), new Vector3(t, wallHeight, depth + t * 2f));
        MakeWall("East",  new Vector3(maxX + t * 0.5f, centerY, (minZ + maxZ) * 0.5f), new Vector3(t, wallHeight, depth + t * 2f));
        MakeWall("South", new Vector3((minX + maxX) * 0.5f, centerY, minZ - t * 0.5f), new Vector3(width + t * 2f, wallHeight, t));
        MakeWall("North", new Vector3((minX + maxX) * 0.5f, centerY, maxZ + t * 0.5f), new Vector3(width + t * 2f, wallHeight, t));
    }

    bool TryGetAreaBounds(out Bounds bounds)
    {
        bounds = new Bounds();
        bool any = false;
        foreach (string rootName in areaRootNames)
        {
            GameObject root = GameObject.Find(rootName);
            if (root == null) continue;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
            {
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
        }
        return any;
    }

    void MakeWall(string wallName, Vector3 center, Vector3 size)
    {
        GameObject go = new GameObject(WallPrefix + wallName);
        go.transform.SetParent(transform, false);
        go.transform.position = center;
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.size = size;
        box.isTrigger = false;
    }

    void ClearWalls()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (!child.name.StartsWith(WallPrefix)) continue;
            if (Application.isPlaying) Destroy(child.gameObject);
            else DestroyImmediate(child.gameObject);
        }
    }

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.35f);
        foreach (BoxCollider box in GetComponentsInChildren<BoxCollider>())
        {
            Gizmos.matrix = box.transform.localToWorldMatrix;
            Gizmos.DrawCube(box.center, box.size);
        }
        Gizmos.matrix = Matrix4x4.identity;
    }
}
