using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Guides the player to the current job. A floating arrow marks the waiting customer (yellow) or
/// the drop-off point (blue), and while driving, arrows on the road show the shortest street
/// route there. Added automatically by RickshawRideSystem; reads the road tiles from CityTraffic.
/// </summary>
public class RideNavigator : MonoBehaviour
{
    [Header("Road arrows")]
    [SerializeField] int arrowCount = 12;
    [SerializeField] float arrowSpacing = 4.5f;
    [SerializeField] float firstArrowDistance = 5f;
    [Tooltip("Seconds between route updates.")]
    [SerializeField] float replanInterval = 0.4f;
    [Tooltip("Road arrows hide once the target is this close (meters).")]
    [SerializeField] float hideArrowsWithin = 12f;

    [Header("Colours")]
    [SerializeField] Color pickupColour = new Color(1f, 0.82f, 0.18f, 1f);
    [SerializeField] Color dropOffColour = new Color(0.25f, 0.85f, 1f, 1f);

    const float GridCell = 6f;

    RickshawRideSystem ride;
    CityTraffic traffic;

    // road graph built from the road tiles
    bool graphReady;
    readonly List<Vector3> nodes = new List<Vector3>();
    readonly List<List<int>> links = new List<List<int>>();
    readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();

    // target marker
    GameObject marker;
    Transform markerArrow;
    Material markerArrowMat, markerRingMat;

    // road arrows
    readonly List<Transform> arrows = new List<Transform>();
    readonly List<Material> arrowMats = new List<Material>();
    readonly List<Vector3> route = new List<Vector3>();
    float nextPlanTime;
    float routeStartS, routeLength, nextTurnNotice;
    GameObject turnArrow;
    Material turnArrowMat;
    Vector3 plannedTarget = new Vector3(float.MaxValue, 0f, float.MaxValue);

    void Start()
    {
        ride = GetComponent<RickshawRideSystem>();
        traffic = FindAnyObjectByType<CityTraffic>();
        BuildMarker();
        BuildArrows();
    }

    void LateUpdate()
    {
        if (ride == null) return;

        Vector3 target;
        bool pickup;
        bool hasTarget = ride.TryGetNavigationTarget(out target, out pickup);
        UpdateMarker(hasTarget, target, pickup);

        Transform vehicle = ride.DrivenVehicle;
        bool showArrows = hasTarget && vehicle != null && EnsureGraph() &&
            FlatDistance(vehicle.position, target) > hideArrowsWithin;
        if (!showArrows)
        {
            SetArrowsVisible(0);
            route.Clear();
            if (turnArrow != null && turnArrow.activeSelf) turnArrow.SetActive(false);
            return;
        }

        if (Time.unscaledTime >= nextPlanTime || FlatDistance(target, plannedTarget) > 1f)
        {
            PlanRoute(vehicle.position, Flat(vehicle.forward), target);
            plannedTarget = target;
            nextPlanTime = Time.unscaledTime + replanInterval;
        }
        Color colour = pickup ? pickupColour : dropOffColour;
        LayArrows(vehicle.position, colour);
        UpdateTurnAround(vehicle, colour);
    }

    // When the route runs the other way, show a big U-turn arrow in front of the car and say so.
    void UpdateTurnAround(Transform vehicle, Color colour)
    {
        bool wrongWay = false;
        if (route.Count >= 2)
        {
            Vector3 pos, dir;
            SampleRoute(Mathf.Min(routeStartS + 3f, Mathf.Max(0f, routeLength - 0.5f)), out pos, out dir);
            wrongWay = Vector3.Dot(Flat(vehicle.forward), dir) < -0.35f;
        }
        if (turnArrow.activeSelf != wrongWay) turnArrow.SetActive(wrongWay);
        if (!wrongWay) return;

        Vector3 forward = Flat(vehicle.forward);
        Vector3 at = vehicle.position + forward * 6f;
        if (route.Count > 0) at.y = route[0].y;
        turnArrow.transform.position = at + Vector3.up * 0.1f;
        turnArrow.transform.rotation = Quaternion.LookRotation(-forward, Vector3.up);
        float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 6f);
        SetColour(turnArrowMat, colour, pulse);

        if (Time.unscaledTime >= nextTurnNotice)
        {
            nextTurnNotice = Time.unscaledTime + 6f;
            ride.ShowNotice("Wrong way - turn around to follow the arrows");
        }
    }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
    }

    // ---------- target marker ----------

    void BuildMarker()
    {
        marker = new GameObject("NavigationTargetMarker");
        marker.transform.SetParent(transform, false);
        markerRingMat = MakeMaterial(pickupColour, 0.35f, true);
        markerArrowMat = MakeMaterial(pickupColour, 0.95f, true);

        GameObject ring = MakeShape(PrimitiveType.Cylinder, marker.transform, markerRingMat);
        ring.name = "Ring";
        ring.transform.localScale = new Vector3(5f, 0.02f, 5f);
        ring.transform.localPosition = new Vector3(0f, 0.1f, 0f);

        GameObject arrow = new GameObject("Arrow");
        arrow.transform.SetParent(marker.transform, false);
        markerArrow = arrow.transform;

        GameObject shaft = MakeShape(PrimitiveType.Cylinder, markerArrow, markerArrowMat);
        shaft.name = "Shaft";
        shaft.transform.localScale = new Vector3(0.45f, 0.7f, 0.45f);
        shaft.transform.localPosition = new Vector3(0f, 2.0f, 0f);

        GameObject head = new GameObject("Head");
        head.transform.SetParent(markerArrow, false);
        head.AddComponent<MeshFilter>().sharedMesh = BuildCone(0.85f, 1.3f, 24);
        head.AddComponent<MeshRenderer>().sharedMaterial = markerArrowMat;

        marker.SetActive(false);
    }

    void UpdateMarker(bool show, Vector3 target, bool pickup)
    {
        if (marker == null) return;
        if (marker.activeSelf != show) marker.SetActive(show);
        if (!show) return;

        Color c = pickup ? pickupColour : dropOffColour;
        SetColour(markerArrowMat, c, 0.95f);
        SetColour(markerRingMat, c, 0.35f);
        marker.transform.position = target;
        // the arrow floats above a standing person and bobs so it catches the eye
        markerArrow.localPosition = new Vector3(0f, 2.6f + Mathf.Sin(Time.unscaledTime * 3f) * 0.35f, 0f);
        markerArrow.Rotate(0f, 90f * Time.unscaledDeltaTime, 0f, Space.Self);
    }

    // ---------- road graph ----------

    bool EnsureGraph()
    {
        if (graphReady) return true;
        if (traffic == null) traffic = FindAnyObjectByType<CityTraffic>();
        if (traffic == null || !traffic.RoutesReady || traffic.RoadTiles.Count == 0) return false;

        IReadOnlyList<Bounds> tiles = traffic.RoadTiles;
        for (int i = 0; i < tiles.Count; i++)
        {
            Bounds b = tiles[i];
            nodes.Add(new Vector3(b.center.x, b.max.y, b.center.z));
            links.Add(new List<int>());
            long key = CellKey(b.center);
            List<int> bucket;
            if (!grid.TryGetValue(key, out bucket)) { bucket = new List<int>(); grid.Add(key, bucket); }
            bucket.Add(i);
        }

        // tiles that touch (allowing small seams) are connected
        for (int i = 0; i < tiles.Count; i++)
        {
            Bounds a = tiles[i];
            int cx = Mathf.FloorToInt(a.center.x / GridCell);
            int cz = Mathf.FloorToInt(a.center.z / GridCell);
            int reach = Mathf.CeilToInt(Mathf.Max(a.size.x, a.size.z) / GridCell) + 1;
            for (int dx = -reach; dx <= reach; dx++)
            for (int dz = -reach; dz <= reach; dz++)
            {
                List<int> bucket;
                if (!grid.TryGetValue(Key(cx + dx, cz + dz), out bucket)) continue;
                for (int k = 0; k < bucket.Count; k++)
                {
                    int j = bucket[k];
                    if (j <= i) continue;
                    Bounds b = tiles[j];
                    bool touchX = Mathf.Abs(a.center.x - b.center.x) <= a.extents.x + b.extents.x + 0.8f;
                    bool touchZ = Mathf.Abs(a.center.z - b.center.z) <= a.extents.z + b.extents.z + 0.8f;
                    if (touchX && touchZ && Mathf.Abs(a.max.y - b.max.y) < 1.5f)
                    {
                        links[i].Add(j);
                        links[j].Add(i);
                    }
                }
            }
        }
        BridgeGaps(tiles);
        graphReady = true;
        return true;
    }

    // Intersections and crossings are often separate meshes, which splits the road tiles into
    // islands. Join every island to its closest neighbour island so a route always exists.
    void BridgeGaps(IReadOnlyList<Bounds> tiles)
    {
        const float maxGap = 16f;
        for (int pass = 0; pass < 64; pass++)
        {
            int[] comp = Components();
            Dictionary<long, int> bestA = new Dictionary<long, int>();
            Dictionary<long, int> bestB = new Dictionary<long, int>();
            Dictionary<long, float> bestD = new Dictionary<long, float>();
            int reach = Mathf.CeilToInt(maxGap / GridCell);
            for (int i = 0; i < nodes.Count; i++)
            {
                int cx = Mathf.FloorToInt(nodes[i].x / GridCell);
                int cz = Mathf.FloorToInt(nodes[i].z / GridCell);
                for (int dx = -reach; dx <= reach; dx++)
                for (int dz = -reach; dz <= reach; dz++)
                {
                    List<int> bucket;
                    if (!grid.TryGetValue(Key(cx + dx, cz + dz), out bucket)) continue;
                    for (int k = 0; k < bucket.Count; k++)
                    {
                        int j = bucket[k];
                        if (comp[i] >= comp[j]) continue;
                        if (Mathf.Abs(nodes[i].y - nodes[j].y) > 1.5f) continue;
                        // tile edge to tile edge gap, so big tiles are not penalised
                        Bounds a = tiles[i], b = tiles[j];
                        float gx = Mathf.Max(0f, Mathf.Abs(a.center.x - b.center.x) - a.extents.x - b.extents.x);
                        float gz = Mathf.Max(0f, Mathf.Abs(a.center.z - b.center.z) - a.extents.z - b.extents.z);
                        float gap = Mathf.Sqrt(gx * gx + gz * gz);
                        if (gap > maxGap) continue;
                        long pair = ((long)comp[i] << 32) | (uint)comp[j];
                        float old;
                        if (!bestD.TryGetValue(pair, out old) || gap < old)
                        {
                            bestD[pair] = gap;
                            bestA[pair] = i;
                            bestB[pair] = j;
                        }
                    }
                }
            }
            if (bestD.Count == 0) return;
            foreach (KeyValuePair<long, float> entry in bestD)
            {
                int a = bestA[entry.Key], b = bestB[entry.Key];
                links[a].Add(b);
                links[b].Add(a);
            }
        }
    }

    int[] Components()
    {
        int[] comp = new int[nodes.Count];
        for (int i = 0; i < comp.Length; i++) comp[i] = -1;
        int next = 0;
        Stack<int> stack = new Stack<int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (comp[i] >= 0) continue;
            comp[i] = next;
            stack.Push(i);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                List<int> l = links[c];
                for (int k = 0; k < l.Count; k++)
                    if (comp[l[k]] < 0) { comp[l[k]] = next; stack.Push(l[k]); }
            }
            next++;
        }
        return comp;
    }

    int NearestNode(Vector3 p)
    {
        int cx = Mathf.FloorToInt(p.x / GridCell);
        int cz = Mathf.FloorToInt(p.z / GridCell);
        for (int r = 0; r <= 8; r++)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue; // only the ring at distance r
                List<int> bucket;
                if (!grid.TryGetValue(Key(cx + dx, cz + dz), out bucket)) continue;
                for (int k = 0; k < bucket.Count; k++)
                {
                    float d = FlatDistance(nodes[bucket[k]], p);
                    if (d < bestD) { bestD = d; best = bucket[k]; }
                }
            }
            if (best >= 0) return best;
        }
        int any = -1;
        float anyD = float.MaxValue;
        for (int i = 0; i < nodes.Count; i++)
        {
            float d = FlatDistance(nodes[i], p);
            if (d < anyD) { anyD = d; any = i; }
        }
        return any;
    }

    // A* over the road tiles
    void PlanRoute(Vector3 from, Vector3 forward, Vector3 to)
    {
        route.Clear();
        // start from the road just ahead of the vehicle, so the route prefers driving on
        // (around the block) over doubling back through the car
        int start = NearestNode(from + forward * 6f);
        if (start < 0) start = NearestNode(from);
        int goal = NearestNode(to);
        if (start < 0 || goal < 0) return;

        int n = nodes.Count;
        float[] g = new float[n];
        int[] parent = new int[n];
        bool[] closed = new bool[n];
        for (int i = 0; i < n; i++) { g[i] = float.MaxValue; parent[i] = -1; }
        MinHeap open = new MinHeap();
        g[start] = 0f;
        open.Push(start, FlatDistance(nodes[start], nodes[goal]));

        while (open.Count > 0)
        {
            int cur = open.Pop();
            if (cur == goal) break;
            if (closed[cur]) continue;
            closed[cur] = true;
            List<int> next = links[cur];
            for (int k = 0; k < next.Count; k++)
            {
                int nb = next[k];
                if (closed[nb]) continue;
                float cost = g[cur] + FlatDistance(nodes[cur], nodes[nb]);
                Vector3 rel = nodes[nb] - from;
                rel.y = 0f;
                if (rel.magnitude < 14f && Vector3.Dot(rel, forward) < -1f)
                    cost += 40f; // going back past the car means a U-turn: only if it is clearly shorter
                if (cost < g[nb])
                {
                    g[nb] = cost;
                    parent[nb] = cur;
                    open.Push(nb, cost + FlatDistance(nodes[nb], nodes[goal]));
                }
            }
        }
        if (start != goal && parent[goal] < 0) return; // no connected road found

        List<Vector3> raw = new List<Vector3>();
        for (int at = goal; at >= 0; at = parent[at])
        {
            raw.Add(nodes[at]);
            if (at == start) break;
        }
        raw.Reverse();

        // smooth the tile-to-tile zigzag into a driving line
        for (int i = 0; i < raw.Count; i++)
        {
            if (i == 0 || i == raw.Count - 1) { route.Add(raw[i]); continue; }
            Vector3 avg = Vector3.zero;
            int count = 0;
            for (int w = -2; w <= 2; w++)
            {
                int j = Mathf.Clamp(i + w, 0, raw.Count - 1);
                avg += raw[j];
                count++;
            }
            route.Add(avg / count);
        }
    }

    // ---------- road arrows ----------

    void BuildArrows()
    {
        Mesh chevron = BuildChevron();

        turnArrow = new GameObject("NavTurnAround");
        turnArrow.transform.SetParent(transform, false);
        turnArrowMat = MakeMaterial(dropOffColour, 0.9f, false);
        GameObject big = new GameObject("Chevron");
        big.transform.SetParent(turnArrow.transform, false);
        big.transform.localScale = new Vector3(2.6f, 1f, 2.6f);
        big.AddComponent<MeshFilter>().sharedMesh = chevron;
        big.AddComponent<MeshRenderer>().sharedMaterial = turnArrowMat;
        turnArrow.SetActive(false);
        for (int i = 0; i < arrowCount; i++)
        {
            GameObject go = new GameObject("NavArrow " + (i + 1));
            go.transform.SetParent(transform, false);
            Material m = MakeMaterial(dropOffColour, 0.85f, false);
            go.AddComponent<MeshFilter>().sharedMesh = chevron;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            arrows.Add(go.transform);
            arrowMats.Add(m);
        }
    }

    void LayArrows(Vector3 vehiclePos, Color colour)
    {
        if (route.Count < 2) { SetArrowsVisible(0); return; }

        // arc length of the point on the route closest to the vehicle
        float startS = 0f, total = 0f, best = float.MaxValue;
        for (int i = 0; i < route.Count - 1; i++)
        {
            Vector3 a = route[i], b = route[i + 1];
            float len = FlatDistance(a, b);
            Vector3 ab = b - a; ab.y = 0f;
            Vector3 ap = vehiclePos - a; ap.y = 0f;
            float t = len > 0.001f ? Mathf.Clamp01(Vector3.Dot(ap, ab) / (len * len)) : 0f;
            float d = (ap - ab * t).magnitude;
            if (d < best) { best = d; startS = total + len * t; }
            total += len;
        }
        routeStartS = startS;
        routeLength = total;

        int shown = 0;
        float s = startS + firstArrowDistance;
        while (shown < arrows.Count && s < total - 1f)
        {
            Vector3 pos, dir;
            SampleRoute(s, out pos, out dir);
            Transform arrow = arrows[shown];
            if (!arrow.gameObject.activeSelf) arrow.gameObject.SetActive(true);
            arrow.position = pos + Vector3.up * 0.08f;
            if (dir.sqrMagnitude > 0.0001f) arrow.rotation = Quaternion.LookRotation(dir, Vector3.up);

            // a bright pulse runs forward along the arrows
            float wave = Mathf.Repeat(Time.unscaledTime * 1.6f - shown * 0.18f, 1f);
            float alpha = Mathf.Lerp(0.45f, 0.95f, Mathf.Clamp01(1f - Mathf.Abs(wave - 0.5f) * 2f));
            SetColour(arrowMats[shown], colour, alpha * Mathf.Lerp(1f, 0.55f, (float)shown / arrows.Count));
            shown++;
            s += arrowSpacing;
        }
        SetArrowsVisible(shown);
    }

    void SampleRoute(float s, out Vector3 pos, out Vector3 dir)
    {
        for (int i = 0; i < route.Count - 1; i++)
        {
            Vector3 a = route[i], b = route[i + 1];
            float len = FlatDistance(a, b);
            if (s <= len || i == route.Count - 2)
            {
                float t = len > 0.001f ? Mathf.Clamp01(s / len) : 0f;
                pos = Vector3.Lerp(a, b, t);
                dir = b - a;
                dir.y = 0f;
                dir.Normalize();
                return;
            }
            s -= len;
        }
        pos = route[route.Count - 1];
        dir = Vector3.forward;
    }

    void SetArrowsVisible(int count)
    {
        for (int i = count; i < arrows.Count; i++)
            if (arrows[i].gameObject.activeSelf) arrows[i].gameObject.SetActive(false);
    }

    // ---------- helpers ----------

    static Material MakeMaterial(Color colour, float alpha, bool alwaysOnTop)
    {
        Shader shader = Shader.Find("UI/Default");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        Material m = new Material(shader);
        SetColour(m, colour, alpha);
        // 8 = Always (marker stays visible through buildings), 4 = LessEqual (arrows sit on the road)
        m.SetInt("unity_GUIZTestMode", alwaysOnTop ? 8 : 4);
        m.renderQueue = alwaysOnTop ? 3500 : 3100;
        return m;
    }

    static void SetColour(Material m, Color c, float alpha)
    {
        if (m == null) return;
        c.a = alpha;
        m.color = c;
    }

    static GameObject MakeShape(PrimitiveType type, Transform parent, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Collider col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        go.transform.SetParent(parent, false);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        // the UI shader multiplies by vertex colour, so give the mesh white vertex colours
        Mesh mesh = go.GetComponent<MeshFilter>().mesh;
        Color[] colors = new Color[mesh.vertexCount];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        mesh.colors = colors;
        return go;
    }

    // flat ">" chevron lying on the road, pointing along +Z
    static Mesh BuildChevron()
    {
        const float halfWidth = 0.8f, length = 0.9f, thickness = 0.38f;
        Vector3[] v =
        {
            new Vector3(-halfWidth, 0f, -length * 0.5f),
            new Vector3(-halfWidth + thickness, 0f, -length * 0.5f),
            new Vector3(0f, 0f, length * 0.5f - thickness * 1.2f),
            new Vector3(0f, 0f, length * 0.5f),
            new Vector3(halfWidth - thickness, 0f, -length * 0.5f),
            new Vector3(halfWidth, 0f, -length * 0.5f),
        };
        int[] tris = { 0, 3, 1, 1, 3, 2, 2, 3, 4, 4, 3, 5 };
        Color[] colors = new Color[v.Length];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        Mesh mesh = new Mesh();
        mesh.vertices = v;
        mesh.colors = colors;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // cone with its tip at the origin, widening upward (a downward-pointing arrow head)
    static Mesh BuildCone(float radius, float height, int segments)
    {
        List<Vector3> verts = new List<Vector3> { Vector3.zero, new Vector3(0f, height, 0f) };
        List<int> tris = new List<int>();
        for (int i = 0; i < segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            verts.Add(new Vector3(Mathf.Cos(a) * radius, height, Mathf.Sin(a) * radius));
        }
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            tris.Add(0); tris.Add(2 + next); tris.Add(2 + i);
            tris.Add(1); tris.Add(2 + i); tris.Add(2 + next);
        }
        Color[] colors = new Color[verts.Count];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        Mesh mesh = new Mesh();
        mesh.SetVertices(verts);
        mesh.colors = colors;
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static long CellKey(Vector3 p)
    {
        return Key(Mathf.FloorToInt(p.x / GridCell), Mathf.FloorToInt(p.z / GridCell));
    }

    static long Key(int x, int z)
    {
        return ((long)x << 32) ^ (uint)z;
    }

    // small binary min-heap for A*
    class MinHeap
    {
        readonly List<int> items = new List<int>();
        readonly List<float> keys = new List<float>();
        public int Count { get { return items.Count; } }

        public void Push(int item, float key)
        {
            items.Add(item);
            keys.Add(key);
            int i = items.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (keys[p] <= keys[i]) break;
                Swap(i, p);
                i = p;
            }
        }

        public int Pop()
        {
            int top = items[0];
            int last = items.Count - 1;
            items[0] = items[last];
            keys[0] = keys[last];
            items.RemoveAt(last);
            keys.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, m = i;
                if (l < items.Count && keys[l] < keys[m]) m = l;
                if (r < items.Count && keys[r] < keys[m]) m = r;
                if (m == i) break;
                Swap(i, m);
                i = m;
            }
            return top;
        }

        void Swap(int a, int b)
        {
            int ti = items[a]; items[a] = items[b]; items[b] = ti;
            float tk = keys[a]; keys[a] = keys[b]; keys[b] = tk;
        }
    }
}
