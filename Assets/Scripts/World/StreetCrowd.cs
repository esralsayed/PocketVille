using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns low-poly characters on the sidewalks. Each person walks one way
/// along a clear path and is removed after leaving the screen. Replacements
/// start off-camera and walk in. Colliders stay off so the crowd does not
/// block the rickshaw. Spawn waits one frame so the mini-map photo is taken first.
/// </summary>
public class StreetCrowd : MonoBehaviour
{
    [Tooltip("Low-poly characters used as pedestrians. Characters 03-10 keep the driver and the fare customer distinct.")]
    [SerializeField] GameObject[] characterPrefabs;
    [Tooltip("How many people to place around the city.")]
    [SerializeField] int walkerCount = 24;
    [Tooltip("Walking speed in meters per second. The walk clip is scaled to match.")]
    [SerializeField] float walkSpeed = 1.2f;

    // Walking.anim covers about 1.47 m/s at animator speed 1.
    const float ClipMetersPerSecond = 1.47f;
    const float MinSpawnSeparation = 7f;
    static readonly int WalkingHash = Animator.StringToHash("Walking");

    class Walker
    {
        public Transform transform;
        public Animator animator;
        public Vector3[] route;
        public int next;
        public int direction;
        public float speed;
        public bool seen;
        public bool finished;
        public float offScreenTime;
    }

    class Node
    {
        public Vector3 point;
        public readonly List<int> neighbors = new List<int>();
    }

    readonly List<Walker> walkers = new List<Walker>();
    readonly List<Vector3[]> routes = new List<Vector3[]>();
    readonly Dictionary<int, float> edgeOffset = new Dictionary<int, float>();
    Transform crowdRoot;
    Camera viewCamera;

    void Start()
    {
        StartCoroutine(SpawnWhenReady());
    }

    IEnumerator SpawnWhenReady()
    {
        yield return null;
        if (characterPrefabs == null || characterPrefabs.Length == 0)
        {
            Debug.LogWarning("StreetCrowd: assign character prefabs.");
            yield break;
        }

        BuildRoutes();
        if (routes.Count == 0)
        {
            Debug.LogWarning("StreetCrowd: no clear sidewalk routes found.");
            yield break;
        }

        crowdRoot = new GameObject("Street Crowd").transform;
        crowdRoot.SetParent(transform, false);
        Transform root = crowdRoot;

        List<int> order = SpreadOrder();
        int placed = 0;
        int guard = 0;
        while (placed < walkerCount && guard < order.Count * 4)
        {
            if (SpawnWalker(root.transform, routes[order[guard % order.Count]]))
                placed++;
            guard++;
        }
    }

    List<int> SpreadOrder()
    {
        List<int> order = new List<int>();
        for (int i = 0; i < routes.Count; i++)
            order.Add(i);
        order.Sort((a, b) =>
        {
            Vector3 ma = routes[a][routes[a].Length / 2];
            Vector3 mb = routes[b][routes[b].Length / 2];
            int ax = Mathf.RoundToInt(ma.x / 25f);
            int az = Mathf.RoundToInt(ma.z / 25f);
            int bx = Mathf.RoundToInt(mb.x / 25f);
            int bz = Mathf.RoundToInt(mb.z / 25f);
            int ha = (ax * 73856093) ^ (az * 19349663);
            int hb = (bx * 73856093) ^ (bz * 19349663);
            return ha.CompareTo(hb);
        });
        return order;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        for (int i = walkers.Count - 1; i >= 0; i--)
        {
            Walker walker = walkers[i];
            if (walker.transform == null)
            {
                walkers.RemoveAt(i);
                continue;
            }

            bool visible = IsVisible(walker.transform.position);
            if (visible)
            {
                walker.seen = true;
                walker.offScreenTime = 0f;
            }
            else if (walker.seen || walker.finished)
            {
                walker.offScreenTime += dt;
                if (walker.offScreenTime > 0.4f)
                {
                    Despawn(i);
                    continue;
                }
            }

            if (walker.finished)
                continue;
            if (!Step(walker, dt))
                continue;

            walker.finished = true;
            if (walker.animator != null)
                walker.animator.SetBool(WalkingHash, false);
        }

        int budget = 2;
        while (crowdRoot != null && walkers.Count < walkerCount && budget > 0)
        {
            if (!SpawnIncoming())
                break;
            budget--;
        }
    }

    void Despawn(int index)
    {
        Walker walker = walkers[index];
        if (walker.transform != null)
            Destroy(walker.transform.gameObject);
        walkers.RemoveAt(index);
    }

    Camera ViewCamera()
    {
        if (viewCamera != null)
            return viewCamera;
        GameObject ride = GameObject.Find("RideCamera");
        if (ride != null)
            viewCamera = ride.GetComponent<Camera>();
        if (viewCamera == null)
            viewCamera = Camera.main;
        return viewCamera;
    }

    bool IsVisible(Vector3 world)
    {
        Camera cam = ViewCamera();
        if (cam == null)
            return true;
        Vector3 viewport = cam.WorldToViewportPoint(world);
        return viewport.z > 0.3f
            && viewport.x > -0.05f && viewport.x < 1.05f
            && viewport.y > -0.05f && viewport.y < 1.05f;
    }

    void BuildRoutes()
    {
        List<Vector3> points = CollectSidewalks();
        edgeOffset.Clear();
        List<Node> nodes = new List<Node>();
        for (int i = 0; i < points.Count; i++)
        {
            if (IsDuplicate(points[i], nodes))
                continue;
            Node node = new Node();
            node.point = SnapToGround(points[i]);
            nodes.Add(node);
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                float offset;
                if (!TryConnect(nodes[i].point, nodes[j].point, out offset))
                    continue;
                nodes[i].neighbors.Add(j);
                nodes[j].neighbors.Add(i);
                edgeOffset[EdgeKey(i, j)] = offset;
            }
        }

        HashSet<int> usedEdges = new HashSet<int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].neighbors.Count == 2)
                continue;
            TraceFrom(nodes, usedEdges, i);
        }
        for (int i = 0; i < nodes.Count; i++)
            TraceFrom(nodes, usedEdges, i);
    }

    static List<Vector3> CollectSidewalks()
    {
        List<Vector3> points = new List<Vector3>();
        GameObject[] roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
        for (int r = 0; r < roots.Length; r++)
        {
            Transform[] transforms = roots[r].GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (!t.name.StartsWith("Sideway"))
                    continue;
                Vector3 p = t.position;
                if (p.y < 60f || p.y > 80f)
                    continue;
                points.Add(p);
            }
        }
        return points;
    }

    static bool IsDuplicate(Vector3 point, List<Node> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (FlatDistance(point, nodes[i].point) < 1.3f)
                return true;
        }
        return false;
    }

    static bool TryConnect(Vector3 a, Vector3 b, out float offset)
    {
        offset = 0f;
        float dx = b.x - a.x;
        float dy = b.y - a.y;
        float dz = b.z - a.z;
        if (dy < 0f) dy = -dy;
        if (dy > 0.6f)
            return false;
        float adx = dx < 0f ? -dx : dx;
        float adz = dz < 0f ? -dz : dz;
        // A diagonal step cuts the corner of a building. Stay on a straight tile edge.
        if (adx > 0.8f && adz > 0.8f)
            return false;
        float distance = Mathf.Sqrt(dx * dx + dz * dz);
        if (distance < 1.6f || distance > 6.5f)
            return false;

        if (SegmentClear(a, b, 0f))
            return true;
        // A lamp or bin on the tile center: step aside if that side is open.
        if (SegmentClear(a, b, 0.55f))
        {
            offset = 0.55f;
            return true;
        }
        if (SegmentClear(a, b, -0.55f))
        {
            offset = -0.55f;
            return true;
        }
        return false;
    }

    static bool SegmentClear(Vector3 a, Vector3 b, float side)
    {
        Vector3 flat = b - a;
        flat.y = 0f;
        float distance = flat.magnitude;
        if (distance < 0.01f)
            return false;
        Vector3 direction = flat / distance;
        Vector3 lateral = Vector3.Cross(Vector3.up, direction);
        float cast = distance - 0.6f;
        if (cast < 0.15f)
            return true;
        Vector3 origin = a + Vector3.up * 1.05f + lateral * side + direction * 0.3f;
        RaycastHit hit;
        return !Physics.SphereCast(origin, 0.2f, direction, out hit, cast, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
    }

    void TraceFrom(List<Node> nodes, HashSet<int> usedEdges, int start)
    {
        List<int> neighbors = nodes[start].neighbors;
        for (int n = 0; n < neighbors.Count; n++)
        {
            int next = neighbors[n];
            if (usedEdges.Contains(EdgeKey(start, next)))
                continue;

            List<Vector3> run = new List<Vector3>();
            int previous = start;
            int current = next;
            usedEdges.Add(EdgeKey(previous, current));
            AppendEdge(run, nodes, previous, current);
            while (true)
            {
                List<int> links = nodes[current].neighbors;
                if (links.Count != 2)
                    break;
                int upcoming = links[0] == previous ? links[1] : links[0];
                if (usedEdges.Contains(EdgeKey(current, upcoming)))
                    break;
                usedEdges.Add(EdgeKey(current, upcoming));
                previous = current;
                current = upcoming;
                AppendEdge(run, nodes, previous, current);
            }
            CommitRoute(run);
        }
    }

    static int EdgeKey(int a, int b)
    {
        int lo = a < b ? a : b;
        int hi = a < b ? b : a;
        return lo * 10000 + hi;
    }

    void AppendEdge(List<Vector3> run, List<Node> nodes, int from, int to)
    {
        float stored = edgeOffset[EdgeKey(from, to)];
        // The offset was measured from the lower index toward the higher one.
        float side = from < to ? stored : -stored;
        Vector3 travel = nodes[to].point - nodes[from].point;
        Vector3 start = ShiftedAlong(nodes[from].point, travel, side);
        Vector3 end = ShiftedAlong(nodes[to].point, travel, side);
        if (run.Count == 0 || FlatDistance(run[run.Count - 1], start) > 0.05f)
            run.Add(start);
        run.Add(end);
    }

    static Vector3 ShiftedAlong(Vector3 point, Vector3 travel, float side)
    {
        travel.y = 0f;
        if (travel.sqrMagnitude < 0.0001f || Mathf.Abs(side) < 0.001f)
            return point;
        return point + Vector3.Cross(Vector3.up, travel.normalized) * side;
    }

    void CommitRoute(List<Vector3> run)
    {
        if (run.Count < 3)
            return;
        float span = FlatDistance(run[0], run[run.Count - 1]);
        if (span < 6f)
            return;
        routes.Add(run.ToArray());
    }

    static Vector3 SnapToGround(Vector3 point)
    {
        RaycastHit hit;
        if (Physics.Raycast(point + Vector3.up * 1.5f, Vector3.down, out hit, 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            if (hit.normal.y > 0.7f && Mathf.Abs(hit.point.y - point.y) < 0.45f)
                return hit.point;
        }
        return point;
    }

    bool SpawnWalker(Transform parent, Vector3[] route)
    {
        GameObject prefab = NextPrefab();
        if (prefab == null)
            return false;

        int start = FarthestFreePoint(route);
        if (start < 0)
            return false;

        float speed = walkSpeed * Random.Range(0.85f, 1.2f);
        Vector3 position = route[start];

        GameObject person = Instantiate(prefab, position, Quaternion.identity, parent);
        person.name = "Pedestrian";

        Collider[] colliders = person.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
            colliders[i].enabled = false;

        Animator animator = person.GetComponent<Animator>();
        if (animator == null)
            animator = person.GetComponentInChildren<Animator>();
        if (animator != null)
        {
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullCompletely;
            animator.SetBool(WalkingHash, true);
            float rate = speed / ClipMetersPerSecond;
            if (rate < 0.6f) rate = 0.6f;
            if (rate > 1.4f) rate = 1.4f;
            animator.speed = rate;
        }

        int direction = 1;
        int next = start + 1;
        if (next >= route.Length)
        {
            direction = -1;
            next = start - 1;
        }
        if (next >= 0 && next < route.Length)
        {
            Vector3 look = route[next] - position;
            look.y = 0f;
            if (look.sqrMagnitude > 0.01f)
                person.transform.rotation = Quaternion.LookRotation(look);
        }

        Walker walker = new Walker();
        walker.transform = person.transform;
        walker.animator = animator;
        walker.route = route;
        walker.next = next;
        walker.direction = direction;
        walker.speed = speed;
        walker.seen = IsVisible(position);
        walkers.Add(walker);
        return true;
    }

    bool SpawnIncoming()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Vector3[] route = routes[Random.Range(0, routes.Count)];
            int stride = route.Length > 16 ? 2 : 1;
            for (int i = 0; i < route.Length; i += stride)
            {
                if (IsVisible(route[i]))
                    continue;
                if (walkers.Count > 0 && Clearance(route[i]) < MinSpawnSeparation)
                    continue;
                int direction = DirectionIntoView(route, i);
                if (direction == 0)
                    continue;
                return SpawnWalkerAt(crowdRoot, route, i, direction);
            }
        }
        return false;
    }

    int DirectionIntoView(Vector3[] route, int index)
    {
        bool forward = EntersView(route, index, 1);
        bool back = EntersView(route, index, -1);
        if (forward && back)
            return Random.value < 0.5f ? 1 : -1;
        if (forward)
            return 1;
        if (back)
            return -1;
        return 0;
    }

    bool EntersView(Vector3[] route, int index, int direction)
    {
        Camera cam = ViewCamera();
        Vector3 focus = cam != null ? cam.transform.position : route[index];
        float startDistance = FlatDistance(route[index], focus);
        for (int step = 1; step <= 30; step++)
        {
            int point = index + direction * step;
            if (point < 0 || point >= route.Length)
                return false;
            if (IsVisible(route[point]))
                return true;
            if (FlatDistance(route[point], focus) + 6f < startDistance)
                return true;
        }
        return false;
    }

    bool SpawnWalkerAt(Transform parent, Vector3[] route, int start, int direction)
    {
        GameObject prefab = NextPrefab();
        if (prefab == null)
            return false;

        float speed = walkSpeed * Random.Range(0.85f, 1.2f);
        int next = start + direction;
        if (next < 0 || next >= route.Length)
            return false;

        Vector3 position = route[start];
        GameObject person = Instantiate(prefab, position, Quaternion.identity, parent);
        person.name = "Pedestrian";
        Collider[] colliders = person.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
            colliders[i].enabled = false;

        Animator animator = person.GetComponent<Animator>();
        if (animator == null)
            animator = person.GetComponentInChildren<Animator>();
        if (animator != null)
        {
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullCompletely;
            animator.SetBool(WalkingHash, true);
            float rate = speed / ClipMetersPerSecond;
            if (rate < 0.6f) rate = 0.6f;
            if (rate > 1.4f) rate = 1.4f;
            animator.speed = rate;
        }

        Vector3 look = route[next] - position;
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f)
            person.transform.rotation = Quaternion.LookRotation(look);

        Walker walker = new Walker();
        walker.transform = person.transform;
        walker.animator = animator;
        walker.route = route;
        walker.next = next;
        walker.direction = direction;
        walker.speed = speed;
        walker.seen = false;
        walkers.Add(walker);
        return true;
    }

    int FarthestFreePoint(Vector3[] route)
    {
        int best = -1;
        float bestClearance = -1f;
        int stride = route.Length > 12 ? 2 : 1;
        for (int i = 0; i < route.Length; i += stride)
        {
            float clearance = Clearance(route[i]);
            if (clearance > bestClearance)
            {
                bestClearance = clearance;
                best = i;
            }
        }
        if (best < 0)
            return -1;
        if (walkers.Count > 0 && bestClearance < MinSpawnSeparation)
            return -1;
        return best;
    }

    float Clearance(Vector3 point)
    {
        float best = 999f;
        for (int i = 0; i < walkers.Count; i++)
        {
            if (walkers[i].transform == null)
                continue;
            float distance = FlatDistance(point, walkers[i].transform.position);
            if (distance < best)
                best = distance;
        }
        return best;
    }

    GameObject NextPrefab()
    {
        for (int attempt = 0; attempt < characterPrefabs.Length; attempt++)
        {
            GameObject prefab = characterPrefabs[Random.Range(0, characterPrefabs.Length)];
            if (prefab != null)
                return prefab;
        }
        return null;
    }

    static bool Step(Walker walker, float dt)
    {
        if (walker.next < 0 || walker.next >= walker.route.Length)
            return true;

        Vector3 target = walker.route[walker.next];
        Vector3 position = walker.transform.position;
        Vector3 flat = target - position;
        flat.y = 0f;
        float distance = flat.magnitude;
        if (distance < 0.35f)
        {
            int upcoming = walker.next + walker.direction;
            if (upcoming < 0 || upcoming >= walker.route.Length)
                return true;
            walker.next = upcoming;
            return false;
        }

        Vector3 step = flat * (walker.speed * dt / distance);
        if (step.magnitude > distance)
            step = flat;
        position += step;
        position.y = Mathf.MoveTowards(position.y, target.y, walker.speed * dt);
        walker.transform.position = position;
        if (flat.sqrMagnitude > 0.01f)
        {
            walker.transform.rotation = Quaternion.Slerp(
                walker.transform.rotation,
                Quaternion.LookRotation(flat),
                1f - Mathf.Exp(-8f * dt));
        }
        return false;
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
