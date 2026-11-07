using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lightweight ambient traffic. Long road meshes become two opposing lanes and
/// non-rideable tuk-tuks travel along them, turning around at each route end.
/// </summary>
public class CityTraffic : MonoBehaviour
{
    [SerializeField] GameObject vehiclePrefab;
    [SerializeField, Range(1, 12)] int vehicleCount = 6;
    [SerializeField] float drivingSpeed = 5f;
    [SerializeField] float turnSpeed = 5f;
    [SerializeField] float obstacleDistance = 7f;
    [SerializeField] float minimumRoadLength = 24f;
    [Tooltip("Rotation needed to make the imported GLB stand upright inside its physics wrapper.")]
    [SerializeField] Vector3 modelRotationCorrection = new Vector3(-90f, 0f, -90f);

    [Header("City cars (Kenney Car Kit, loaded from a Resources folder)")]
    [Tooltip("Resources folder with the car models. Leave empty for tuk-tuks only.")]
    [SerializeField] string carModelsResourcesPath = "KenneyVehicles";
    [Tooltip("Share of the NPC vehicles that are cars instead of tuk-tuks.")]
    [SerializeField, Range(0f, 1f)] float carShare = 0.5f;
    [Tooltip("Length of the sedan in meters. All car models share one scale, so trucks stay bigger than cars.")]
    [SerializeField] float sedanLength = 4.2f;
    [Tooltip("Extra turn (degrees) for the car models. Set 180 if the cars drive backwards.")]
    [SerializeField] float carModelYaw = 0f;

    GameObject[] carModels = new GameObject[0];
    float carScale = 1f;
    int nextCarModel;

    class Lane
    {
        public Vector3 a;
        public Vector3 b;
        public float Length { get { return Vector3.Distance(a, b); } }
    }

    class Car
    {
        public Transform transform;
        public Rigidbody body;
        public Lane lane;
        public bool towardB;
        public int laneIndex;
        public float respawnAt;
    }

    readonly List<Lane> lanes = new List<Lane>();
    readonly List<Car> cars = new List<Car>();
    Transform trafficRoot;
    Transform playerVehicle;
    Camera viewCamera;
    TrafficLightSystem trafficLights;
    float baseDrivingSpeed;

    /// <summary>A straight stretch of road found in the city, shared with systems such as speed radars.</summary>
    public struct RoadRoute
    {
        public Vector3 start;
        public Vector3 end;
        public float width;
        public float Length { get { return Vector3.Distance(start, end); } }
    }

    readonly List<RoadRoute> routes = new List<RoadRoute>();
    readonly List<Bounds> roadTileBounds = new List<Bounds>();

    /// <summary>World bounds of every road tile, used for route finding and sidewalk detection.</summary>
    public IReadOnlyList<Bounds> RoadTiles { get { return roadTileBounds; } }

    /// <summary>Closest point on any road tile to p (on the road surface). False until roads are mapped.</summary>
    public bool TryGetNearestRoadPoint(Vector3 p, out Vector3 roadPoint)
    {
        roadPoint = p;
        float best = float.MaxValue;
        for (int i = 0; i < roadTileBounds.Count; i++)
        {
            Bounds b = roadTileBounds[i];
            Vector3 q = new Vector3(Mathf.Clamp(p.x, b.min.x + 0.4f, b.max.x - 0.4f), b.max.y, Mathf.Clamp(p.z, b.min.z + 0.4f, b.max.z - 0.4f));
            float dx = q.x - p.x, dz = q.z - p.z;
            float d = dx * dx + dz * dz;
            if (d < best) { best = d; roadPoint = q; }
        }
        return best < float.MaxValue;
    }

    /// <summary>Road routes found while building lanes. Empty until RoutesReady is true.</summary>
    public IReadOnlyList<RoadRoute> Routes { get { return routes; } }
    public bool RoutesReady { get; private set; }

    void Awake()
    {
        baseDrivingSpeed = drivingSpeed;
    }

    public void SetDifficulty(float speedMultiplier)
    {
        drivingSpeed = baseDrivingSpeed * Mathf.Clamp(speedMultiplier, 0.7f, 1.6f);
    }

    IEnumerator Start()
    {
        // Let the gameplay system capture its clean mini-map before adding traffic.
        yield return null;
        yield return null;

        trafficLights = FindAnyObjectByType<TrafficLightSystem>();

        // Map the roads first so other systems (speed radars) can use them even without traffic.
        BuildLanes();
        RoutesReady = true;

        if (vehiclePrefab == null)
        {
            Debug.LogWarning("CityTraffic: no vehicle prefab assigned.");
            yield break;
        }

        if (lanes.Count == 0)
        {
            Debug.LogWarning("CityTraffic: no suitable road lanes were found.");
            yield break;
        }

        trafficRoot = new GameObject("NPC Traffic").transform;
        trafficRoot.SetParent(transform, false);
        LoadCarModels();
        SpawnCars();
    }

    void BuildLanes()
    {
        List<Bounds> roadTiles = new List<Bounds>();
        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            string objectName = renderer.name.ToLowerInvariant();
            if (objectName != "street" && !objectName.StartsWith("street ("))
                continue;

            Bounds bounds = renderer.bounds;
            if (bounds.center.y < 55f || bounds.center.y > 85f)
                continue;
            if (bounds.size.x < 0.5f || bounds.size.z < 0.5f)
                continue;

            roadTiles.Add(bounds);
        }

        // This city kit builds a road from many small square renderers rather than
        // one long mesh. Stitch touching tiles on shared rows and columns into routes.
        roadTileBounds.Clear();
        roadTileBounds.AddRange(roadTiles);
        BuildAxisRoutes(roadTiles, true);
        BuildAxisRoutes(roadTiles, false);

        Transform player = null;
        TukTukController controller = FindAnyObjectByType<TukTukController>();
        if (controller != null) player = controller.transform;
        playerVehicle = player;
        Vector3 reference = player != null ? player.position : transform.position;
        lanes.Sort((x, y) =>
            DistanceToLane(reference, x).CompareTo(DistanceToLane(reference, y)));
    }

    void BuildAxisRoutes(List<Bounds> tiles, bool alongX)
    {
        Dictionary<int, List<Bounds>> rows = new Dictionary<int, List<Bounds>>();
        for (int i = 0; i < tiles.Count; i++)
        {
            Bounds tile = tiles[i];
            float cross = alongX ? tile.center.z : tile.center.x;
            int key = Mathf.RoundToInt(cross * 2f); // half-metre grouping tolerance
            if (!rows.TryGetValue(key, out List<Bounds> row))
            {
                row = new List<Bounds>();
                rows.Add(key, row);
            }
            row.Add(tile);
        }

        foreach (List<Bounds> row in rows.Values)
        {
            row.Sort((a, b) =>
                (alongX ? a.min.x : a.min.z).CompareTo(alongX ? b.min.x : b.min.z));
            if (row.Count == 0) continue;

            float start = alongX ? row[0].min.x : row[0].min.z;
            float end = alongX ? row[0].max.x : row[0].max.z;
            float crossTotal = alongX ? row[0].center.z : row[0].center.x;
            float widthTotal = alongX ? row[0].size.z : row[0].size.x;
            float surfaceY = row[0].max.y;
            int joined = 1;

            for (int i = 1; i <= row.Count; i++)
            {
                bool atEnd = i == row.Count;
                float nextStart = atEnd ? float.PositiveInfinity : (alongX ? row[i].min.x : row[i].min.z);
                if (!atEnd && nextStart <= end + 0.75f)
                {
                    end = Mathf.Max(end, alongX ? row[i].max.x : row[i].max.z);
                    crossTotal += alongX ? row[i].center.z : row[i].center.x;
                    widthTotal += alongX ? row[i].size.z : row[i].size.x;
                    surfaceY = Mathf.Max(surfaceY, row[i].max.y);
                    joined++;
                    continue;
                }

                AddRouteSegment(alongX, start, end, crossTotal / joined,
                    surfaceY + 0.08f, widthTotal / joined);
                if (!atEnd)
                {
                    start = nextStart;
                    end = alongX ? row[i].max.x : row[i].max.z;
                    crossTotal = alongX ? row[i].center.z : row[i].center.x;
                    widthTotal = alongX ? row[i].size.z : row[i].size.x;
                    surfaceY = row[i].max.y;
                    joined = 1;
                }
            }
        }
    }

    void AddRouteSegment(bool alongX, float start, float end, float cross, float y, float width)
    {
        float length = end - start;
        if (length < minimumRoadLength)
            return;

        RoadRoute route = new RoadRoute();
        route.start = alongX ? new Vector3(start, y, cross) : new Vector3(cross, y, start);
        route.end = alongX ? new Vector3(end, y, cross) : new Vector3(cross, y, end);
        route.width = width;
        routes.Add(route);

        float inset = Mathf.Min(4f, length * 0.08f);
        float laneOffset = Mathf.Clamp(width * 0.22f, 0.65f, 1.5f);
        Vector3 a = alongX
            ? new Vector3(start + inset, y, cross + laneOffset)
            : new Vector3(cross + laneOffset, y, start + inset);
        Vector3 b = alongX
            ? new Vector3(end - inset, y, cross + laneOffset)
            : new Vector3(cross + laneOffset, y, end - inset);
        Vector3 oppositeA = alongX
            ? new Vector3(end - inset, y, cross - laneOffset)
            : new Vector3(cross - laneOffset, y, end - inset);
        Vector3 oppositeB = alongX
            ? new Vector3(start + inset, y, cross - laneOffset)
            : new Vector3(cross - laneOffset, y, start + inset);

        AddLane(a, b);
        AddLane(oppositeA, oppositeB);
    }

    static float DistanceToLane(Vector3 point, Lane lane)
    {
        Vector3 segment = lane.b - lane.a;
        float denominator = segment.sqrMagnitude;
        float t = denominator > 0.001f
            ? Mathf.Clamp01(Vector3.Dot(point - lane.a, segment) / denominator)
            : 0f;
        return Vector3.Distance(point, lane.a + segment * t);
    }

    void AddLane(Vector3 a, Vector3 b)
    {
        for (int i = 0; i < lanes.Count; i++)
        {
            if (Vector3.Distance(lanes[i].a, a) < 2f && Vector3.Distance(lanes[i].b, b) < 2f)
                return;
        }
        Lane lane = new Lane();
        lane.a = a;
        lane.b = b;
        lanes.Add(lane);
    }

    void SpawnCars()
    {
        int wanted = Mathf.Min(vehicleCount, lanes.Count);
        for (int i = 0; i < wanted; i++)
        {
            int laneIndex = InitialLaneIndex(i, wanted);
            bool isCar = UsesCarModel(i);
            string label = (isCar ? "NPC Car " : "NPC TukTuk ") + (i + 1);
            GameObject go = new GameObject(label);
            go.transform.SetParent(trafficRoot, false);
            go.name = label;

            GameObject source = vehiclePrefab;
            if (isCar)
            {
                source = carModels[nextCarModel % carModels.Length];
                nextCarModel++;
            }
            GameObject visual = Instantiate(source, go.transform);
            visual.name = "Visual";
            visual.transform.localPosition = Vector3.zero;
            // Import correction makes the GLB upright; the extra yaw aligns its
            // nose with the wrapper's forward axis and therefore with its collider.
            visual.transform.localRotation = Quaternion.Euler(0f, 90f, 0f)
                * Quaternion.Euler(modelRotationCorrection);
            visual.transform.localScale = Vector3.one;

            Vector3 carSize = Vector3.zero;
            if (isCar)
                carSize = FitCarModel(visual.transform);

            // The ambient vehicles are intentionally not driveable or fuel-consuming.
            foreach (TukTukController controller in visual.GetComponentsInChildren<TukTukController>(true))
                controller.enabled = false;
            foreach (TukTukFuelSystem fuel in visual.GetComponentsInChildren<TukTukFuelSystem>(true))
                fuel.enabled = false;
            foreach (Collider existing in visual.GetComponentsInChildren<Collider>(true))
                existing.enabled = false;
            foreach (Rigidbody existing in visual.GetComponentsInChildren<Rigidbody>(true))
            {
                existing.isKinematic = true;
                existing.detectCollisions = false;
            }

            BoxCollider box = go.AddComponent<BoxCollider>();
            if (isCar)
            {
                box.center = new Vector3(0f, carSize.y * 0.5f, 0f);
                box.size = carSize;
            }
            else
            {
                box.center = new Vector3(0f, 0.9f, 0f);
                box.size = new Vector3(1.25f, 1.8f, 2.15f);
            }

            Rigidbody body = go.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            // every skin except the player's classic yellow, so the busier traffic looks varied
            if (!isCar)
            {
                RickshawSkins.Skin skin = RickshawSkins.All[1 + i % (RickshawSkins.All.Length - 1)];
                RickshawSkins.Apply(visual.transform, "body", skin);
            }

            Car car = new Car();
            car.transform = go.transform;
            car.body = body;
            cars.Add(car);
            PlaceCar(car, laneIndex, i < 2, i);
        }
    }

    void LoadCarModels()
    {
        carModels = new GameObject[0];
        if (string.IsNullOrEmpty(carModelsResourcesPath) || carShare <= 0f)
            return;
        carModels = Resources.LoadAll<GameObject>(carModelsResourcesPath);
        if (carModels.Length == 0)
            return;

        // One shared scale for every model: the sedan (or the first model) becomes sedanLength long.
        GameObject reference = carModels[0];
        for (int i = 0; i < carModels.Length; i++)
            if (carModels[i].name == "sedan") reference = carModels[i];
        Bounds b = LocalBounds(reference.transform, reference.transform);
        float length = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        carScale = length > 0.01f ? sedanLength / length : 1f;

        // shuffle once so the mix of models differs between plays
        for (int i = carModels.Length - 1; i > 0; i--)
        {
            int k = Random.Range(0, i + 1);
            GameObject t = carModels[i];
            carModels[i] = carModels[k];
            carModels[k] = t;
        }
    }

    // Spreads the cars evenly through the traffic (carShare 0.5 = every second vehicle).
    bool UsesCarModel(int slot)
    {
        if (carModels.Length == 0 || carShare <= 0f) return false;
        return Mathf.FloorToInt((slot + 1) * carShare) > Mathf.FloorToInt(slot * carShare);
    }

    // Stands a car model upright, points its length along the wrapper's forward axis, scales it
    // and sets it on the road centred on the wrapper. Returns its size for the collider.
    Vector3 FitCarModel(Transform visual)
    {
        Transform wrapper = visual.parent;
        visual.localPosition = Vector3.zero;
        visual.localRotation = Quaternion.Euler(0f, carModelYaw, 0f);
        visual.localScale = Vector3.one * carScale;
        Bounds b = LocalBounds(wrapper, visual);

        if (b.size.y > b.size.x && b.size.y > b.size.z)
        {
            // exported Z-up: tip it upright
            visual.localRotation = Quaternion.Euler(0f, carModelYaw, 0f) * Quaternion.Euler(-90f, 0f, 0f);
            b = LocalBounds(wrapper, visual);
        }
        if (b.size.x > b.size.z * 1.15f)
        {
            // length lies sideways: turn it to face forward
            visual.localRotation = Quaternion.Euler(0f, 90f, 0f) * visual.localRotation;
            b = LocalBounds(wrapper, visual);
        }

        visual.localPosition = new Vector3(-b.center.x, -b.min.y, -b.center.z);
        Vector3 size = b.size;
        size.x = Mathf.Max(size.x, 0.5f);
        size.y = Mathf.Max(size.y, 0.5f);
        size.z = Mathf.Max(size.z, 0.5f);
        return size;
    }

    // Bounds of every mesh under model, expressed in space's local coordinates. Works on prefab
    // assets too, because it reads mesh bounds instead of renderer bounds.
    static Bounds LocalBounds(Transform space, Transform model)
    {
        bool any = false;
        Bounds result = new Bounds(Vector3.zero, Vector3.zero);
        Matrix4x4 toSpace = space.worldToLocalMatrix;
        foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null) continue;
            Encapsulate(ref result, ref any, toSpace * filter.transform.localToWorldMatrix, filter.sharedMesh.bounds);
        }
        foreach (SkinnedMeshRenderer skinned in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (skinned.sharedMesh == null) continue;
            Encapsulate(ref result, ref any, toSpace * skinned.transform.localToWorldMatrix, skinned.sharedMesh.bounds);
        }
        return result;
    }

    static void Encapsulate(ref Bounds result, ref bool any, Matrix4x4 matrix, Bounds mesh)
    {
        Vector3 min = mesh.min, max = mesh.max;
        for (int c = 0; c < 8; c++)
        {
            Vector3 corner = new Vector3((c & 1) == 0 ? min.x : max.x, (c & 2) == 0 ? min.y : max.y, (c & 4) == 0 ? min.z : max.z);
            Vector3 p = matrix.MultiplyPoint3x4(corner);
            if (!any) { result = new Bounds(p, Vector3.zero); any = true; }
            else result.Encapsulate(p);
        }
    }

    int InitialLaneIndex(int slot, int count)
    {
        if (slot < 2 || count <= 2)
            return Mathf.Min(slot, lanes.Count - 1);
        float spread = (slot - 1f) / Mathf.Max(1f, count - 2f);
        return Mathf.Clamp(Mathf.RoundToInt(spread * (lanes.Count - 1)), 0, lanes.Count - 1);
    }

    void PlaceCar(Car car, int laneIndex, bool nearPlayer, int slot)
    {
        laneIndex = Mathf.Clamp(laneIndex, 0, lanes.Count - 1);
        Lane lane = lanes[laneIndex];
        float fraction;
        Vector3 reference = playerVehicle != null ? playerVehicle.position : transform.position;
        if (nearPlayer)
        {
            Vector3 segment = lane.b - lane.a;
            fraction = segment.sqrMagnitude > 0.001f
                ? Vector3.Dot(reference - lane.a, segment) / segment.sqrMagnitude
                : 0.5f;
            float offset = (slot % 2 == 0 ? 1f : -1f) * (14f + slot * 4f);
            fraction = Mathf.Clamp(fraction + offset / Mathf.Max(1f, lane.Length), 0.1f, 0.9f);
            Vector3 candidate = Vector3.Lerp(lane.a, lane.b, fraction);
            if (Vector3.Distance(candidate, reference) < 9f)
                fraction = Mathf.Clamp(fraction - offset * 2f / Mathf.Max(1f, lane.Length), 0.1f, 0.9f);
        }
        else
        {
            fraction = Random.Range(0.15f, 0.85f);
        }

        car.laneIndex = laneIndex;
        car.lane = lane;
        car.towardB = true;
        car.respawnAt = Time.time + Random.Range(40f, 75f);
        Vector3 direction = (lane.b - lane.a).normalized;
        car.body.position = Vector3.Lerp(lane.a, lane.b, fraction);
        car.body.rotation = Quaternion.LookRotation(direction, Vector3.up);
        car.transform.SetPositionAndRotation(car.body.position, car.body.rotation);
    }

    void RespawnCar(Car car, int slot)
    {
        Vector3 oldCenter = (car.lane.a + car.lane.b) * 0.5f;
        int chosen = car.laneIndex;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            int candidate = Random.Range(0, lanes.Count);
            Vector3 candidateCenter = (lanes[candidate].a + lanes[candidate].b) * 0.5f;
            if (candidate != car.laneIndex && Vector3.Distance(oldCenter, candidateCenter) > 55f)
            {
                chosen = candidate;
                break;
            }
        }
        PlaceCar(car, chosen, false, slot);
    }

    bool IsVisible(Vector3 world)
    {
        if (viewCamera == null)
        {
            GameObject rideCamera = GameObject.Find("RideCamera");
            if (rideCamera != null) viewCamera = rideCamera.GetComponent<Camera>();
            if (viewCamera == null) viewCamera = Camera.main;
        }
        if (viewCamera == null) return false;
        Vector3 viewport = viewCamera.WorldToViewportPoint(world);
        return viewport.z > 0f && viewport.x > -0.15f && viewport.x < 1.15f
            && viewport.y > -0.15f && viewport.y < 1.15f;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        for (int i = 0; i < cars.Count; i++)
        {
            Car car = cars[i];
            if (car.transform == null || car.body == null) continue;

            if ((Time.time >= car.respawnAt || car.body.position.y < 50f || car.body.position.y > 90f)
                && !IsVisible(car.body.position))
            {
                RespawnCar(car, i);
                continue;
            }

            Vector3 target = car.towardB ? car.lane.b : car.lane.a;
            Vector3 delta = target - car.body.position;
            delta.y = 0f;
            if (delta.sqrMagnitude < 4f)
            {
                if (!IsVisible(car.body.position))
                {
                    RespawnCar(car, i);
                    continue;
                }
                car.towardB = !car.towardB;
                target = car.towardB ? car.lane.b : car.lane.a;
                delta = target - car.body.position;
                delta.y = 0f;
            }
            if (delta.sqrMagnitude < 0.01f) continue;

            Vector3 direction = delta.normalized;
            float speed = RoadAheadIsClear(car, direction) ? drivingSpeed : 0f;
            Quaternion desired = Quaternion.LookRotation(direction, Vector3.up);
            Quaternion rotation = Quaternion.Slerp(car.body.rotation, desired, 1f - Mathf.Exp(-turnSpeed * dt));
            car.body.MoveRotation(rotation);
            if (speed > 0f)
                car.body.MovePosition(car.body.position + direction * (speed * dt));
        }
    }

    bool RoadAheadIsClear(Car car, Vector3 direction)
    {
        if (trafficLights != null && trafficLights.ShouldStopForRed(
                car.body.position,
                direction,
                obstacleDistance + 5f))
            return false;

        RaycastHit[] hits = Physics.SphereCastAll(
            car.body.position + Vector3.up * 0.8f,
            0.55f,
            direction,
            obstacleDistance,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            Transform hit = hits[i].transform;
            if (hit == null || hit == car.transform || hit.IsChildOf(car.transform))
                continue;
            if (hits[i].distance < 0.2f)
                continue;
            return false;
        }
        return true;
    }
}
