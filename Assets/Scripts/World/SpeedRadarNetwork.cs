using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Places speed radars on the longest straight roads once CityTraffic has mapped them.
/// To edit its settings, keep a "Speed Radars" object with this component in the city scene
/// (Tools > PocketVille > Set Up Editable Features). Without one it creates itself with the
/// defaults below. If the scene has radars placed by hand (GameObject > PocketVille > Speed Radar),
/// those are used and nothing is placed automatically.
/// </summary>
public class SpeedRadarNetwork : MonoBehaviour
{
    [Header("Automatic placement")]
    [Tooltip("Place radars on the longest roads. Skipped when the scene already has hand-placed radars.")]
    [SerializeField] bool autoPlaceRadars = true;
    [SerializeField, Range(0, 10)] int radarCount = 3;
    [Tooltip("Only straight roads at least this long get a radar (meters).")]
    [SerializeField] float minimumRouteLength = 40f;
    [Tooltip("Radars are kept at least this far apart (meters).")]
    [SerializeField] float minimumSpacing = 70f;
    [Tooltip("No radar this close to where the player's tuk-tuk starts (meters).")]
    [SerializeField] float clearanceFromStart = 30f;

    [Header("Rules for automatic radars")]
    [SerializeField] int speedLimitKmh = 25;
    [SerializeField] int fineAmount = 15;
    [Tooltip("Seconds before the same radar can fine again.")]
    [SerializeField] float cooldown = 3f;
    [Tooltip("Warn the driver this far before a radar (meters). 0 = no warning.")]
    [SerializeField] float warningDistance = 70f;

    [Header("Look of automatic radars")]
    [SerializeField] bool paintRoadStripes = true;
    [SerializeField] bool showLabel = true;
    [Tooltip("Tell the player how many radars are active when the shift starts.")]
    [SerializeField] bool announceAtStart = true;

    static readonly Color PoleColor = new Color(0.22f, 0.23f, 0.25f, 1f);
    static readonly Color HousingColor = new Color(0.82f, 0.83f, 0.80f, 1f);
    static readonly Color SignRed = new Color(0.82f, 0.10f, 0.10f, 1f);
    static readonly Color SignWhite = new Color(0.96f, 0.96f, 0.94f, 1f);

    IReadOnlyList<Bounds> roadTiles;
    readonly List<Vector3> used = new List<Vector3>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (FindAnyObjectByType<SpeedRadarNetwork>() != null) return; // one placed in the scene wins
        if (FindAnyObjectByType<CityTraffic>() == null) return;
        GameObject go = new GameObject("Speed Radars");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<SpeedRadarNetwork>();
    }

    IEnumerator Start()
    {
        int handPlaced = FindObjectsByType<SpeedRadar>(FindObjectsSortMode.None).Length;
        int placed = 0;

        if (autoPlaceRadars && handPlaced == 0 && radarCount > 0)
        {
            CityTraffic traffic = FindAnyObjectByType<CityTraffic>();
            float giveUpAt = Time.realtimeSinceStartup + 20f;
            while (traffic != null && !traffic.RoutesReady && Time.realtimeSinceStartup < giveUpAt)
                yield return null;

            if (traffic == null || !traffic.RoutesReady || traffic.Routes.Count == 0)
            {
                Debug.LogWarning("SpeedRadarNetwork: no road routes were found, so no speed radars were placed.");
                yield break;
            }

            roadTiles = traffic.RoadTiles;
            placed = PlaceRadars(traffic.Routes, minimumRouteLength, minimumSpacing);
            // a small city may not have enough long roads: relax the rules until all radars are placed
            if (placed < radarCount) placed += PlaceRadars(traffic.Routes, minimumRouteLength * 0.6f, minimumSpacing * 0.6f);
            if (placed < radarCount) placed += PlaceRadars(traffic.Routes, 15f, 25f);
            Debug.Log("SpeedRadarNetwork: placed " + placed + " speed radar(s), limit " + speedLimitKmh + " km/h.");
        }
        else if (handPlaced > 0)
        {
            Debug.Log("SpeedRadarNetwork: using " + handPlaced + " hand-placed speed radar(s).");
        }

        yield return null; // let hand-placed radars finish their Start
        int total = SpeedRadar.Count;
        RickshawRideSystem ride = FindAnyObjectByType<RickshawRideSystem>();
        if (announceAtStart && ride != null && total > 0)
            ride.ShowNotice(total + " speed radars are active - look for S on the mini map");
    }

    int PlaceRadars(IReadOnlyList<CityTraffic.RoadRoute> routes, float minLength, float spacing)
    {
        List<CityTraffic.RoadRoute> candidates = new List<CityTraffic.RoadRoute>();
        for (int i = 0; i < routes.Count; i++)
            if (routes[i].Length >= minLength)
                candidates.Add(routes[i]);
        candidates.Sort((a, b) => b.Length.CompareTo(a.Length));

        TukTukController player = FindAnyObjectByType<TukTukController>();
        Vector3 start = player != null ? player.transform.position : new Vector3(float.MaxValue, 0f, float.MaxValue);

        int before = used.Count;
        for (int i = 0; i < candidates.Count && used.Count < radarCount; i++)
        {
            CityTraffic.RoadRoute route = candidates[i];
            Vector3 spot;
            if (!FindSpotOnRoute(route, routes, out spot)) continue;
            if (FlatDistance(spot, start) < clearanceFromStart) continue;

            bool tooClose = false;
            for (int j = 0; j < used.Count; j++)
                if (FlatDistance(spot, used[j]) < spacing) { tooClose = true; break; }
            if (tooClose) continue;

            BuildRadar(route, spot, used.Count + 1);
            used.Add(spot);
        }
        return used.Count - before;
    }

    // A point along the route that is not inside a crossing road, so cars driving across an
    // intersection are never measured by mistake.
    static bool FindSpotOnRoute(CityTraffic.RoadRoute route, IReadOnlyList<CityTraffic.RoadRoute> all, out Vector3 spot)
    {
        float[] fractions = { 0.5f, 0.4f, 0.6f, 0.3f, 0.7f };
        for (int f = 0; f < fractions.Length; f++)
        {
            Vector3 candidate = Vector3.Lerp(route.start, route.end, fractions[f]);
            bool inCrossing = false;
            for (int i = 0; i < all.Count; i++)
            {
                CityTraffic.RoadRoute other = all[i];
                Vector3 a = other.end - other.start;
                Vector3 b = route.end - route.start;
                if (a.sqrMagnitude < 0.01f || b.sqrMagnitude < 0.01f) continue;
                if (Mathf.Abs(Vector3.Dot(a.normalized, b.normalized)) > 0.5f) continue; // parallel, not crossing
                if (DistanceToSegment(candidate, other.start, other.end) < other.width * 0.5f + 3f)
                {
                    inCrossing = true;
                    break;
                }
            }
            if (!inCrossing)
            {
                spot = candidate;
                return true;
            }
        }
        spot = Vector3.zero;
        return false;
    }

    void BuildRadar(CityTraffic.RoadRoute route, Vector3 spot, int number)
    {
        Vector3 along = route.end - route.start;
        along.y = 0f;
        along.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, along);

        // The city builds a road from several rows of tiles, so measure the real road edges
        // from the tiles instead of trusting one row's width.
        float rightEdge = EdgeDistance(spot, side, route.width * 0.5f);
        float leftEdge = EdgeDistance(spot, -side, route.width * 0.5f);
        Vector3 roadCentre = spot + side * ((rightEdge - leftEdge) * 0.5f);
        float roadWidth = rightEdge + leftEdge;

        GameObject root = new GameObject("Speed Radar " + number);
        root.transform.SetParent(transform, false);
        root.transform.SetPositionAndRotation(roadCentre, Quaternion.LookRotation(along, Vector3.up));

        // detection zone across the whole road (local x = across, local z = along the road)
        BoxCollider zone = root.AddComponent<BoxCollider>();
        zone.isTrigger = true;
        zone.center = new Vector3(0f, 1.5f, 0f);
        zone.size = new Vector3(roadWidth + 1f, 4f, 4f);

        Renderer lamp = BuildRadarModel(root.transform, roadWidth, roadWidth * 0.5f + 1.1f,
            speedLimitKmh, paintRoadStripes, showLabel);
        SpeedRadar radar = root.AddComponent<SpeedRadar>();
        radar.Configure(speedLimitKmh, fineAmount, cooldown, warningDistance, lamp);
    }

    /// <summary>
    /// Builds the radar's look under root: a camera post on the sidewalk postOffset meters to the
    /// right of root, speed-limit signs both ways, an optional label and optional road stripes.
    /// Returns the lamp that flashes on a fine. Used for automatic and hand-placed radars.
    /// </summary>
    public static Renderer BuildRadarModel(Transform root, float roadWidth, float postOffset, int limitKmh, bool stripes, bool label)
    {
        Vector3 postBase = GroundAt(root.TransformPoint(new Vector3(postOffset, 0f, 0f)), root.position.y);
        Transform post = new GameObject("Post").transform;
        post.SetParent(root, false);
        post.SetPositionAndRotation(postBase, root.rotation);

        GameObject pole = MakePart(PrimitiveType.Cylinder, post, "Pole", new Vector3(0f, 1.6f, 0f), new Vector3(0.14f, 1.6f, 0.14f), Quaternion.identity, PoleColor, true);
        pole.GetComponent<Collider>().isTrigger = false;

        MakePart(PrimitiveType.Cube, post, "CameraHousing", new Vector3(-0.25f, 3.25f, 0f), new Vector3(0.55f, 0.38f, 0.75f), Quaternion.identity, HousingColor, false);
        GameObject lampFront = MakePart(PrimitiveType.Cube, post, "LampFront", new Vector3(-0.25f, 3.25f, -0.39f), new Vector3(0.24f, 0.24f, 0.04f), Quaternion.identity, Color.red, false);
        GameObject lampBack = MakePart(PrimitiveType.Cube, post, "LampBack", new Vector3(-0.25f, 3.25f, 0.39f), new Vector3(0.24f, 0.24f, 0.04f), Quaternion.identity, Color.red, false);
        Renderer lamp = lampFront.GetComponent<Renderer>();
        lampBack.GetComponent<Renderer>().sharedMaterial = lamp.sharedMaterial; // both lamps flash together

        // a speed-limit sign facing each direction of travel
        BuildSign(post, new Vector3(0f, 2.35f, -0.09f), false, limitKmh);
        BuildSign(post, new Vector3(0f, 2.35f, 0.09f), true, limitKmh);

        if (stripes && roadWidth > 0.5f)
        {
            // red-and-white bands across the road at both ends of the zone
            for (int e = -1; e <= 1; e += 2)
            {
                for (int s = 0; s < 6; s++)
                {
                    float across = -roadWidth * 0.5f + roadWidth * (s + 0.5f) / 6f;
                    MakePart(PrimitiveType.Cube, root, "ZoneStripe",
                        new Vector3(across, 0.03f, e * 2f), new Vector3(roadWidth / 6f, 0.02f, 0.45f),
                        Quaternion.identity, s % 2 == 0 ? SignRed : SignWhite, false);
                }
            }
        }

        if (label)
        {
            BuildLabel(post, new Vector3(-0.25f, 3.85f, 0f), true);
            BuildLabel(post, new Vector3(-0.25f, 3.85f, 0f), false);
        }
        return lamp;
    }

    // Distance from point to the road edge along dir, found by stepping until we leave the road tiles.
    float EdgeDistance(Vector3 point, Vector3 dir, float fallback)
    {
        if (roadTiles == null || roadTiles.Count == 0) return fallback;
        float lastOnRoad = 0f;
        for (float s = 0.25f; s <= 25f; s += 0.25f)
        {
            if (OnRoad(point + dir * s)) lastOnRoad = s;
            else if (s - lastOnRoad > 1.25f) break; // small seams between tiles are still road
        }
        return Mathf.Max(lastOnRoad + 0.25f, 1f);
    }

    bool OnRoad(Vector3 p)
    {
        for (int i = 0; i < roadTiles.Count; i++)
        {
            Bounds b = roadTiles[i];
            if (p.x >= b.min.x - 0.05f && p.x <= b.max.x + 0.05f && p.z >= b.min.z - 0.05f && p.z <= b.max.z + 0.05f)
                return true;
        }
        return false;
    }

    static void BuildLabel(Transform post, Vector3 localPos, bool facesForward)
    {
        if (TMP_Settings.defaultFontAsset == null) return;
        float faceDir = facesForward ? 1f : -1f;
        GameObject textObj = new GameObject("RadarLabel");
        textObj.transform.SetParent(post, false);
        textObj.transform.localPosition = localPos;
        textObj.transform.localRotation = Quaternion.LookRotation(new Vector3(0f, 0f, -faceDir), Vector3.up);
        TextMeshPro text = textObj.AddComponent<TextMeshPro>();
        text.text = "SPEED RADAR";
        text.font = TMP_Settings.defaultFontAsset;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = new Color(1f, 0.55f, 0.1f, 1f);
        text.rectTransform.sizeDelta = new Vector2(2.4f, 0.5f);
        text.enableAutoSizing = true;
        text.fontSizeMin = 0.5f;
        text.fontSizeMax = 4f;
        text.textWrappingMode = TextWrappingModes.NoWrap;
    }

    static void BuildSign(Transform post, Vector3 localPos, bool facesForward, int limitKmh)
    {
        // A face toward -z is seen by drivers travelling +z, and the other way round.
        float faceDir = facesForward ? 1f : -1f;
        Quaternion disc = Quaternion.Euler(90f, 0f, 0f);
        MakePart(PrimitiveType.Cylinder, post, "SignRim", localPos, new Vector3(0.82f, 0.02f, 0.82f), disc, SignRed, false);
        MakePart(PrimitiveType.Cylinder, post, "SignFace", localPos + new Vector3(0f, 0f, 0.025f * faceDir), new Vector3(0.62f, 0.02f, 0.62f), disc, SignWhite, false);

        if (TMP_Settings.defaultFontAsset == null) return;
        GameObject textObj = new GameObject("SignText");
        textObj.transform.SetParent(post, false);
        textObj.transform.localPosition = localPos + new Vector3(0f, 0f, 0.055f * faceDir);
        // TextMeshPro is readable from behind its +z axis
        textObj.transform.localRotation = Quaternion.LookRotation(new Vector3(0f, 0f, -faceDir), Vector3.up);
        TextMeshPro text = textObj.AddComponent<TextMeshPro>();
        text.text = limitKmh.ToString();
        text.font = TMP_Settings.defaultFontAsset;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.black;
        text.rectTransform.sizeDelta = new Vector2(0.55f, 0.4f);
        text.enableAutoSizing = true;
        text.fontSizeMin = 0.5f;
        text.fontSizeMax = 4f;
        text.textWrappingMode = TextWrappingModes.NoWrap;
    }

    static GameObject MakePart(PrimitiveType type, Transform parent, string name, Vector3 localPos, Vector3 localScale, Quaternion localRot, Color colour, bool keepCollider)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        go.transform.localScale = localScale;
        if (!keepCollider)
            Destroy(go.GetComponent<Collider>());

        Renderer r = go.GetComponent<Renderer>();
        Material m = new Material(r.sharedMaterial);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
        if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
        r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        return go;
    }

    static Vector3 GroundAt(Vector3 point, float fallbackY)
    {
        RaycastHit hit;
        Vector3 from = new Vector3(point.x, fallbackY + 6f, point.z);
        if (Physics.Raycast(from, Vector3.down, out hit, 14f, ~0, QueryTriggerInteraction.Ignore))
            return hit.point;
        return new Vector3(point.x, fallbackY, point.z);
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        p.y = 0f; a.y = 0f; b.y = 0f;
        Vector3 ab = b - a;
        float t = ab.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
        return Vector3.Distance(p, a + ab * t);
    }
}
