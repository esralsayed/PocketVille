using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// One self-contained, vehicle-agnostic system for: walking on foot, boarding / leaving ANY
/// registered vehicle, picking up and dropping off customers, refuelling at gas stations,
/// on-screen guidance, controls help and fares.
///
/// It lives on a single new root object and never edits other scene objects' saved data.
/// At runtime it only:
///  - enables / disables each vehicle's drive controller (so a vehicle only drives while ridden),
///  - slows parked vehicles' Rigidbodies so they do not slide away,
///  - calls TukTukFuelSystem.Refuel and MoneyManager (AddMoney for fares, SpendMoney for fuel).
/// Everything it spawns (driver, customers, camera, UI, stall marker) is parented under this object.
///
/// ADDING A NEW VEHICLE LATER: either add an entry to the 'Vehicles' list in the Inspector, or call
/// RegisterVehicle(driveController, "Name") from the vehicle's own script. Nothing else changes.
/// The drive controller is any Behaviour that moves the vehicle from input; the fuel system
/// (TukTukFuelSystem) and Rigidbody are found automatically on the controller's object or parents.
///
/// Keys: WASD / arrows = walk or drive, Enter = ride / get off, P = pick up / drop off,
///       Space = handbrake (existing), hold right mouse + drag = rotate camera on foot.
/// </summary>
public class RickshawRideSystem : MonoBehaviour
{
    [System.Serializable]
    public class RideVehicle
    {
        public string displayName = "Vehicle";
        [Tooltip("Behaviour that drives the vehicle from input. Enabled only while ridden.")]
        public Behaviour driveController;
        [Tooltip("Optional. Found automatically on the controller's object or its parents.")]
        public TukTukFuelSystem fuel;
        [Tooltip("Optional. Found automatically on the controller's object or its parents.")]
        public Rigidbody body;
        [Tooltip("Seat height above ground (lower = sits deeper in the vehicle).")]
        public float seatHeight = 0.25f;
        public Vector3 driverSeatOffset = Vector3.zero;
        public Vector3 passengerSeatOffset = Vector3.zero;
        [Tooltip("Name of the material that gets recolored by skins (the yellow body).")]
        public string bodyMaterialName = "body";
        [Tooltip("Apply the skin equipped in the item shop to this vehicle.")]
        public bool useEquippedSkin = true;

        // fall-zone bookkeeping (runtime only)
        [System.NonSerialized] public Vector3 lastSafePos;
        [System.NonSerialized] public Quaternion lastSafeRot = Quaternion.identity;
        [System.NonSerialized] public float nextSafeTime;

        [System.NonSerialized] public Vector3 driverSeatLocal;
        [System.NonSerialized] public Vector3 passengerSeatLocal;

        public Transform Transform { get { return driveController != null ? driveController.transform : null; } }
    }

    struct Landmark
    {
        public string label;
        public Vector3 position;
    }

    struct FuelStall
    {
        public string label;
        public Vector3 position;
    }

    [Header("Prefabs")]
    [SerializeField] GameObject driverPrefab;
    [SerializeField] GameObject customerPrefab;

    [Header("Vehicles (empty = auto-detect every TukTukController in the scene)")]
    [SerializeField] List<RideVehicle> vehicles = new List<RideVehicle>();

    [Header("Scene lookups (by name, read-only)")]
    [SerializeField] string busStopName = "Bus stop prefab";
    [SerializeField] string[] landmarkNames = { "ATM_prefab", "phone booth prefab (3)", "Hydrant prefab (15)", "Mail_box prefab (2)", "Bus stop prefab" };
    [SerializeField] string[] landmarkLabels = { "the ATM", "the phone booths", "the hydrants", "the mailboxes", "the bus stop" };

    [Header("Ranges (meters)")]
    [SerializeField] float boardRange = 5f;
    [SerializeField] float pickupRange = 7f;
    [SerializeField] float dropRange = 10f;
    [SerializeField] float maxActionSpeed = 2f;
    [SerializeField] float minTripDistance = 60f;

    [Header("On foot")]
    [SerializeField] float walkSpeed = 4.5f;
    [SerializeField] float turnSpeed = 12f;

    [Header("Fare")]
    [Tooltip("Coins paid for every completed ride.")]
    [SerializeField] int rideFare = 50;

    // ---------- fall zone ----------
    // If a vehicle (or the player on foot) falls far below the roads, put it back on the road at the last
    // spot where it was safely on the ground.
    [Header("Fall zone")]
    [Tooltip("Scene object (by name) whose lowest point marks the road level.")]
    [SerializeField] string fallReferenceName = "Roads & Infrastructure";
    [Tooltip("Falling this far below the lowest road triggers the rescue (meters).")]
    [SerializeField] float fallDepth = 25f;

    float fallY = float.NaN;
    Vector3 driverSafePos;
    float nextDriverSafeTime;

    void ComputeFallLevel()
    {
        float lowest = float.MaxValue;
        GameObject reference = GameObject.Find(fallReferenceName);
        if (reference != null)
            foreach (Renderer r in reference.GetComponentsInChildren<Renderer>())
                lowest = Mathf.Min(lowest, r.bounds.min.y);
        if (lowest == float.MaxValue)
            lowest = vehicles[0].Transform.position.y;
        fallY = lowest - fallDepth;
    }

    void CheckFalls()
    {
        if (vehicles.Count == 0) return;
        if (float.IsNaN(fallY)) ComputeFallLevel();

        foreach (RideVehicle v in vehicles)
        {
            Transform vt = v.Transform;
            if (vt == null || v.body == null) continue;
            Vector3 pos = v.body.position;

            if (pos.y < fallY)
            {
                // rescue: back to the last safe spot, stopped
                v.body.linearVelocity = Vector3.zero;
                v.body.angularVelocity = Vector3.zero;
                v.body.position = v.lastSafePos;
                v.body.rotation = v.lastSafeRot;
                Physics.SyncTransforms();
                v.nextSafeTime = Time.time + 1f;
                if (v == current) Toast("You fell off the map - back on the road!");
                continue;
            }

            // remember where this vehicle was last safely on the road
            if (Time.time >= v.nextSafeTime && vt.up.y > 0.7f)
            {
                v.nextSafeTime = Time.time + 0.5f;
                RaycastHit hit;
                if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out hit, 2.5f, ~0, QueryTriggerInteraction.Ignore)
                    && hit.transform.root != vt.root && !hit.transform.IsChildOf(transform))
                {
                    v.lastSafePos = pos + Vector3.up * 0.3f;
                    v.lastSafeRot = Quaternion.LookRotation(FlatForward(vt));
                }
            }
        }

        // the player on foot
        if (current == null && driver != null)
        {
            if (driver.transform.position.y < fallY)
            {
                driverController.enabled = false;
                driver.transform.position = driverSafePos;
                driverController.enabled = true;
                Toast("You fell off the map - back on the road!");
            }
            else if (Time.time >= nextDriverSafeTime && driverController.enabled && driverController.isGrounded)
            {
                nextDriverSafeTime = Time.time + 0.5f;
                driverSafePos = driver.transform.position;
            }
        }
    }

    // ---------- mini map ----------
    // One top-down snapshot of the city is rendered once at start (cheap), then shown in a round window that
    // scrolls and rotates with the camera. Blips: customer, drop-off, gas station, vehicles.
    [Header("Mini map")]
    [Tooltip("Scene objects (by name) whose combined size defines the map area.")]
    [SerializeField] string[] mapRootNames = { "Roads & Infrastructure", "Buildings", "Environment" };
    [SerializeField] float mapMargin = 25f;
    [SerializeField] int mapTextureSize = 2048;
    [Tooltip("On-screen diameter of the round map (reference pixels).")]
    [SerializeField] float mapDiameterUi = 300f;
    [Tooltip("Radius of the area shown (meters). M switches between these.")]
    [SerializeField] float[] mapZoomRadii = { 55f, 110f };
    [SerializeField] Vector2 mapScreenOffset = new Vector2(30f, 30f);

    class Blip
    {
        public RectTransform rt;
        public Image fill;
        public GameObject go;
    }

    RenderTexture mapTexture;
    Vector2 mapCenter;       // world x,z of the middle of the snapshot
    Vector2 mapWorldSize;    // world meters covered by the snapshot
    bool mapReady;
    int mapZoomIndex;
    RectTransform mapRotator, mapImageRt, northRt;
    Blip customerBlip, destinationBlip;
    readonly List<Blip> stallBlips = new List<Blip>();
    readonly List<Blip> vehicleBlips = new List<Blip>();
    readonly List<Blip> radarBlips = new List<Blip>();
    Transform mapBlipRoot;
    Font mapBlipFont;
    static Sprite circleSprite, ringSprite, arrowSprite;

    void BuildMapTexture()
    {
        Bounds b = new Bounds();
        bool any = false;
        foreach (string rootName in mapRootNames)
        {
            GameObject root = GameObject.Find(rootName);
            if (root == null) continue;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
        }
        if (!any)
        {
            Debug.LogWarning("RickshawRideSystem: no map roots found; the mini map is disabled.");
            return;
        }

        float w = b.size.x + mapMargin * 2f;
        float h = b.size.z + mapMargin * 2f;
        mapCenter = new Vector2(b.center.x, b.center.z);
        mapWorldSize = new Vector2(w, h);

        int texW, texH;
        if (w >= h) { texW = mapTextureSize; texH = Mathf.Max(64, Mathf.RoundToInt(mapTextureSize * h / w)); }
        else { texH = mapTextureSize; texW = Mathf.Max(64, Mathf.RoundToInt(mapTextureSize * w / h)); }

        mapTexture = new RenderTexture(texW, texH, 24, RenderTextureFormat.ARGB32);
        mapTexture.name = "MiniMapTexture";
        mapTexture.useMipMap = true;
        mapTexture.autoGenerateMips = true;
        mapTexture.filterMode = FilterMode.Trilinear;
        mapTexture.Create();

        GameObject camObj = new GameObject("MiniMapCapture");
        camObj.transform.SetParent(transform, false);
        camObj.transform.position = new Vector3(mapCenter.x, 400f, mapCenter.y);
        camObj.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // looking straight down, screen-up = north (+Z)
        Camera cam = camObj.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = h * 0.5f;
        cam.aspect = w / h;
        cam.nearClipPlane = 1f;
        cam.farClipPlane = 800f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
        cam.enabled = false;

        UnityEngine.Rendering.Universal.UniversalAdditionalCameraData data =
            camObj.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
        data.renderPostProcessing = false;

        // hide the vehicles while taking the picture (they are drawn as blips instead)
        List<Renderer> hidden = new List<Renderer>();
        foreach (RideVehicle v in vehicles)
        {
            if (v.Transform == null) continue;
            foreach (Renderer r in v.Transform.GetComponentsInChildren<Renderer>())
                if (r.enabled) { r.enabled = false; hidden.Add(r); }
        }

        cam.enabled = true;
        UnityEngine.Rendering.RenderPipeline.StandardRequest request = new UnityEngine.Rendering.RenderPipeline.StandardRequest();
        request.destination = mapTexture;
        if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(cam, request))
        {
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, request);
        }
        else
        {
            cam.targetTexture = mapTexture;
            cam.Render();
            cam.targetTexture = null;
        }
        cam.enabled = false;

        foreach (Renderer r in hidden) if (r != null) r.enabled = true;
        Destroy(camObj);
    }

    static Sprite BuildSprite(int n, System.Func<Vector2, float> alphaAt)
    {
        Color32[] px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float a = Mathf.Clamp01(alphaAt(new Vector2((x + 0.5f) / n, (y + 0.5f) / n)));
                px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.SetPixels32(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
    }

    static Sprite GetCircleSprite()
    {
        if (circleSprite == null)
            circleSprite = BuildSprite(128, p => (0.5f - Vector2.Distance(p, new Vector2(0.5f, 0.5f))) * 128f / 1.5f);
        return circleSprite;
    }

    static Sprite GetRingSprite()
    {
        if (ringSprite == null)
            ringSprite = BuildSprite(256, p =>
            {
                float d = Vector2.Distance(p, new Vector2(0.5f, 0.5f));
                return Mathf.Min((0.5f - d) * 256f / 1.5f, (d - 0.465f) * 256f / 1.5f);
            });
        return ringSprite;
    }

    static Sprite GetArrowSprite()
    {
        if (arrowSprite == null)
        {
            Vector2[] poly = { new Vector2(0.5f, 0.96f), new Vector2(0.88f, 0.08f), new Vector2(0.5f, 0.28f), new Vector2(0.12f, 0.08f) };
            arrowSprite = BuildSprite(128, p => PointInPolygon(p, poly) ? 1f : 0f);
        }
        return arrowSprite;
    }

    Blip MakeBlip(Transform parent, string blipName, Color color, string letter, Font font)
    {
        RectTransform outer = MakePanel(parent, blipName, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(34f, 34f));
        Image outerImg = outer.GetComponent<Image>();
        outerImg.sprite = GetCircleSprite();
        outerImg.color = Color.white;

        RectTransform inner = MakePanel(outer, "Fill", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(26f, 26f));
        Image innerImg = inner.GetComponent<Image>();
        innerImg.sprite = GetCircleSprite();
        innerImg.color = color;

        Text t = MakeText(inner, font, 18, FontStyle.Bold, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero);
        t.text = letter;
        t.color = Color.white;
        t.resizeTextForBestFit = false;

        Blip blip = new Blip();
        blip.rt = outer;
        blip.fill = innerImg;
        blip.go = outer.gameObject;
        blip.go.SetActive(false);
        return blip;
    }

    void BuildMiniMapUi(Transform canvas, Font font)
    {
        if (mapTexture == null) return;

        float d = mapDiameterUi;
        RectTransform root = MakePanel(canvas, "MiniMap", new Vector2(0f, 0f), mapScreenOffset, new Vector2(d, d));
        Image back = root.GetComponent<Image>();
        back.sprite = GetCircleSprite();
        back.color = new Color(0.04f, 0.05f, 0.08f, 0.92f);

        RectTransform maskRt = MakePanel(root, "MapMask", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(d - 14f, d - 14f));
        Image maskImg = maskRt.GetComponent<Image>();
        maskImg.sprite = GetCircleSprite();
        maskImg.color = Color.white;
        Mask mask = maskRt.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        GameObject rotGo = new GameObject("MapRotator", typeof(RectTransform));
        rotGo.transform.SetParent(maskRt, false);
        mapRotator = (RectTransform)rotGo.transform;
        mapRotator.anchorMin = new Vector2(0.5f, 0.5f);
        mapRotator.anchorMax = new Vector2(0.5f, 0.5f);
        mapRotator.sizeDelta = Vector2.zero;

        GameObject imgGo = new GameObject("MapImage", typeof(RectTransform), typeof(RawImage));
        imgGo.transform.SetParent(mapRotator, false);
        mapImageRt = (RectTransform)imgGo.transform;
        mapImageRt.anchorMin = new Vector2(0.5f, 0.5f);
        mapImageRt.anchorMax = new Vector2(0.5f, 0.5f);
        mapImageRt.pivot = new Vector2(0.5f, 0.5f);
        RawImage raw = imgGo.GetComponent<RawImage>();
        raw.texture = mapTexture;
        raw.color = new Color(0.82f, 0.86f, 0.95f, 1f); // slightly dimmed so the blips stand out
        raw.raycastTarget = false;

        RectTransform ring = MakePanel(root, "MapRing", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(d, d));
        Image ringImg = ring.GetComponent<Image>();
        ringImg.sprite = GetRingSprite();
        ringImg.color = new Color(0.96f, 0.78f, 0.25f, 1f);

        // blips (drawn on top of the ring so they stay readable at the edge)
        customerBlip = MakeBlip(root, "CustomerBlip", new Color(0.95f, 0.75f, 0.05f, 1f), "$", font);
        destinationBlip = MakeBlip(root, "DestinationBlip", new Color(0.15f, 0.7f, 0.3f, 1f), "D", font);
        for (int i = 0; i < stalls.Count; i++)
            stallBlips.Add(MakeBlip(root, "GasBlip" + i, new Color(0.85f, 0.2f, 0.2f, 1f), "F", font));
        for (int i = 0; i < vehicles.Count; i++)
            vehicleBlips.Add(MakeBlip(root, "VehicleBlip" + i, new Color(0.2f, 0.5f, 0.9f, 1f), "R", font));
        mapBlipRoot = root;
        mapBlipFont = font;

        RectTransform arrow = MakePanel(root, "PlayerArrow", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(34f, 34f));
        Image arrowImg = arrow.GetComponent<Image>();
        arrowImg.sprite = GetArrowSprite();
        arrowImg.color = Color.white;

        RectTransform north = MakePanel(root, "North", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(30f, 30f));
        north.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        Text nt = MakeText(north, font, 24, FontStyle.Bold, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero);
        nt.text = "N";
        nt.color = new Color(1f, 0.35f, 0.3f, 1f);
        northRt = north;

        mapReady = true;
    }

    void PlaceBlip(Blip blip, bool show, Vector3 world, Vector3 player, float scale, float cos, float sin, float edge)
    {
        if (blip.go.activeSelf != show) blip.go.SetActive(show);
        if (!show) return;

        Vector2 delta = new Vector2((world.x - player.x) * scale, (world.z - player.z) * scale);
        Vector2 r = new Vector2(delta.x * cos - delta.y * sin, delta.x * sin + delta.y * cos);
        float len = r.magnitude;
        bool clamped = len > edge;
        if (clamped) r = r / len * edge;
        blip.rt.anchoredPosition = r;
        Color c = blip.fill.color;
        c.a = clamped ? 0.8f : 1f;
        blip.fill.color = c;
    }

    void RefreshMiniMap()
    {
        if (!mapReady || driver == null) return;

        Vector3 player = current != null ? current.Transform.position : driver.transform.position;
        float radius = Mathf.Max(10f, mapZoomRadii[mapZoomIndex % mapZoomRadii.Length]);
        float scale = mapDiameterUi / (2f * radius); // UI pixels per meter
        float yaw = camYaw;                           // the map turns so "up" is where the camera looks
        float rad = yaw * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);

        mapRotator.localRotation = Quaternion.Euler(0f, 0f, yaw);
        mapImageRt.sizeDelta = mapWorldSize * scale;
        mapImageRt.anchoredPosition = -new Vector2(player.x - mapCenter.x, player.z - mapCenter.y) * scale;

        float edge = mapDiameterUi * 0.5f - 20f;

        PlaceBlip(customerBlip, customer != null && !hasPassenger, customer != null ? customer.transform.position : player, player, scale, cos, sin, edge);
        PlaceBlip(destinationBlip, hasPassenger, destination, player, scale, cos, sin, edge);

        bool lowFuel = IsLowOnFuel();
        float pulse = lowFuel ? 1f + 0.25f * Mathf.Sin(Time.time * 6f) : 1f;
        for (int i = 0; i < stallBlips.Count && i < stalls.Count; i++)
        {
            PlaceBlip(stallBlips[i], true, stalls[i].position, player, scale, cos, sin, edge);
            stallBlips[i].rt.localScale = new Vector3(pulse, pulse, 1f);
        }
        for (int i = 0; i < vehicleBlips.Count && i < vehicles.Count; i++)
        {
            Transform vt = vehicles[i].Transform;
            PlaceBlip(vehicleBlips[i], current == null && vt != null, vt != null ? vt.position : player, player, scale, cos, sin, edge);
        }

        // speed radars (created as they appear, since the radars are placed a moment after start)
        while (mapBlipRoot != null && radarBlips.Count < SpeedRadar.Count)
        {
            Blip rb = MakeBlip(mapBlipRoot, "RadarBlip" + radarBlips.Count, new Color(1f, 0.45f, 0.05f, 1f), "S", mapBlipFont);
            rb.rt.SetSiblingIndex(customerBlip.rt.GetSiblingIndex()); // stay under the player arrow
            rb.rt.localScale = new Vector3(0.85f, 0.85f, 1f);
            radarBlips.Add(rb);
        }
        for (int i = 0; i < radarBlips.Count; i++)
            PlaceBlip(radarBlips[i], i < SpeedRadar.Count, i < SpeedRadar.Count ? SpeedRadar.PositionOf(i) : player, player, scale, cos, sin, edge);

        // little "N" riding on the rim, pointing to north
        northRt.anchoredPosition = new Vector2(-sin, cos) * (mapDiameterUi * 0.5f - 4f);
    }

    // ---------- ratings & tips ----------
    [Header("Ratings & tips")]
    [Tooltip("Typical driving speed (m/s) used to work out how long a ride should take.")]
    [SerializeField] float cruiseSpeed = 8f;
    [Tooltip("Time allowed = base + factor x distance / cruise speed.")]
    [SerializeField] float timeBudgetFactor = 1.8f;
    [SerializeField] float timeBudgetBase = 20f;
    [Tooltip("Up to this much over the time limit (0.3 = 30%) costs 1 star; more costs 2.")]
    [SerializeField] float lateTolerance = 0.3f;
    [Tooltip("A speed drop faster than this (m/s^2) counts as a crash. Normal braking is 18, handbrake 36.")]
    [SerializeField] float crashDeceleration = 80f;
    [Tooltip("A crash only counts if the vehicle was going at least this fast (m/s).")]
    [SerializeField] float crashMinSpeed = 3f;
    [SerializeField, Range(0f, 1f)] float tipFiveStars = 0.5f;
    [SerializeField, Range(0f, 1f)] float tipFourStars = 0.25f;

    public struct RideResult
    {
        public int stars;
        public int fare;
        public int tip;
        public int crashes;
        public float seconds;
        public float budget;
    }

    /// <summary>Raised after every completed ride (levels / daily goals hook in here).</summary>
    public event System.Action<RideResult> RideCompleted;
    public int RidesCompleted { get; private set; }
    public float AverageStars { get { return RidesCompleted == 0 ? 0f : (float)totalStars / RidesCompleted; } }

    int totalStars, rideCrashes;
    float rideStartTime, rideTimeBudget, lastCrashTime = -10f, previousSpeed;
    float resultUntil;
    GameObject resultPanel;
    Image[] resultStars;
    Text resultTitle, resultMoney, resultNotes;
    static Sprite starSprite;

    void BeginRide()
    {
        rideStartTime = Time.time;
        rideCrashes = 0;
        previousSpeed = VehicleSpeed(current);
        float distance = FlatDistance(tripStart, destination);
        rideTimeBudget = timeBudgetBase + timeBudgetFactor * distance / Mathf.Max(1f, cruiseSpeed);
    }

    // a crash = the speed of the ridden vehicle collapses much faster than braking can explain
    void DetectCrash()
    {
        if (current == null)
        {
            previousSpeed = 0f;
            return;
        }

        float speed = VehicleSpeed(current);
        float dt = Time.fixedDeltaTime;
        if (hasPassenger && dt > 0f && previousSpeed > crashMinSpeed
            && (previousSpeed - speed) / dt > crashDeceleration
            && Time.time - lastCrashTime > 1f)
        {
            rideCrashes++;
            lastCrashTime = Time.time;
            Toast("Ouch! Your customer felt that.");
        }
        previousSpeed = speed;
    }

    RideResult ScoreRide(int fare)
    {
        float seconds = Time.time - rideStartTime;

        int stars = 5;
        if (seconds > rideTimeBudget * (1f + lateTolerance)) stars -= 2;
        else if (seconds > rideTimeBudget) stars -= 1;
        if (rideCrashes >= 2) stars -= 2;
        else if (rideCrashes == 1) stars -= 1;
        stars = Mathf.Clamp(stars, 1, 5);

        float tipRate = stars == 5 ? tipFiveStars : (stars == 4 ? tipFourStars : 0f);

        RideResult r = new RideResult();
        r.stars = stars;
        r.fare = fare;
        r.tip = Mathf.RoundToInt(fare * tipRate);
        r.crashes = rideCrashes;
        r.seconds = seconds;
        r.budget = rideTimeBudget;

        RidesCompleted++;
        totalStars += stars;
        if (RideCompleted != null) RideCompleted(r);
        return r;
    }

    static string FormatTime(float seconds)
    {
        int t = Mathf.Max(0, Mathf.CeilToInt(seconds));
        return (t / 60) + ":" + (t % 60).ToString("00");
    }

    string RideTimeText()
    {
        float left = rideTimeBudget - (Time.time - rideStartTime);
        if (left >= 0f) return "Time left " + FormatTime(left);
        return "<color=#FF5555>LATE  +" + FormatTime(-left) + "</color>";
    }

    void ShowRideResult(RideResult r)
    {
        if (resultPanel == null) return;

        for (int i = 0; i < resultStars.Length; i++)
            resultStars[i].color = i < r.stars ? new Color(1f, 0.82f, 0.2f, 1f) : new Color(1f, 1f, 1f, 0.18f);

        resultTitle.text = r.stars >= 5 ? "PERFECT RIDE!" : (r.stars == 4 ? "GREAT RIDE" : (r.stars == 3 ? "RIDE COMPLETE" : "ROUGH RIDE"));
        resultMoney.text = "Fare +" + r.fare + (r.tip > 0 ? "      <color=#7CFF8A>Tip +" + r.tip + "</color>" : "      No tip");

        string timeNote = r.seconds <= r.budget
            ? "On time (" + FormatTime(r.seconds) + ")"
            : "Late (" + FormatTime(r.seconds) + " of " + FormatTime(r.budget) + ")";
        string crashNote = r.crashes == 0 ? "No crashes" : r.crashes + (r.crashes == 1 ? " crash" : " crashes");
        resultNotes.text = timeNote + "   -   " + crashNote;

        resultPanel.SetActive(true);
        resultUntil = Time.time + 6f;
    }

    void UpdateRideResultUi()
    {
        if (resultPanel != null && resultPanel.activeSelf && Time.time > resultUntil)
            resultPanel.SetActive(false);
    }

    void BuildResultUi(Transform canvas, Font font)
    {
        RectTransform panel = MakePanel(canvas, "RideResultPanel", new Vector2(0.5f, 0.5f), new Vector2(0f, 110f), new Vector2(820f, 330f));
        panel.GetComponent<Image>().color = new Color(0.05f, 0.07f, 0.12f, 0.92f);

        RectTransform title = MakePanel(panel, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(780f, 70f));
        title.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        resultTitle = MakeText(title, font, 48, FontStyle.Bold, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero);

        resultStars = new Image[5];
        for (int i = 0; i < 5; i++)
        {
            RectTransform star = MakePanel(panel, "Star" + i, new Vector2(0.5f, 1f), new Vector2((i - 2) * 96f, -92f), new Vector2(84f, 84f));
            Image img = star.GetComponent<Image>();
            img.sprite = GetStarSprite();
            img.color = new Color(1f, 1f, 1f, 0.18f);
            img.raycastTarget = false;
            resultStars[i] = img;
        }

        RectTransform money = MakePanel(panel, "Money", new Vector2(0.5f, 1f), new Vector2(0f, -190f), new Vector2(780f, 60f));
        money.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        resultMoney = MakeText(money, font, 40, FontStyle.Bold, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero);
        resultMoney.color = new Color(1f, 0.84f, 0.3f, 1f);

        RectTransform notes = MakePanel(panel, "Notes", new Vector2(0.5f, 1f), new Vector2(0f, -254f), new Vector2(780f, 50f));
        notes.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        resultNotes = MakeText(notes, font, 28, FontStyle.Normal, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero);

        resultPanel = panel.gameObject;
        resultPanel.SetActive(false);
    }

    // a white 5-point star, tinted at runtime (the UI font has no star glyph)
    static Sprite GetStarSprite()
    {
        if (starSprite != null) return starSprite;

        const int n = 128;
        Vector2[] poly = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float angle = Mathf.PI / 2f + i * Mathf.PI / 5f;
            float radius = (i % 2 == 0) ? 0.48f : 0.20f;
            poly[i] = new Vector2(0.5f + Mathf.Cos(angle) * radius, 0.52f + Mathf.Sin(angle) * radius);
        }

        Color32[] px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                Vector2 p = new Vector2((x + 0.5f) / n, (y + 0.5f) / n);
                px[y * n + x] = PointInPolygon(p, poly) ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            }

        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.SetPixels32(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.Apply();
        starSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        return starSprite;
    }

    static bool PointInPolygon(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            if (((poly[i].y > p.y) != (poly[j].y > p.y)) &&
                (p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x))
                inside = !inside;
        }
        return inside;
    }

    // ---------- contracts ----------
    // Every city visit offers three different contracts. Completing one raises the driver's rank and
    // generates a harder set. Failing or leaving restores the coins held when the contract started.
    static int levelsCompletedWithoutProfile; // playing the city scene directly, with no profile

    RickshawLevels.Level level;
    RickshawLevels.Level[] contractOffers;
    int levelNumber, levelStartMoney;
    int contractCrashes, contractViolations;
    bool levelRunning, levelPanelOpen;
    float levelTimeLeft;
    TrafficLightSystem trafficLights;
    GameObject levelHud, levelPanel;
    Text levelHudText, levelPanelTitle, levelPanelBody;
    readonly List<GameObject> levelButtons = new List<GameObject>();

    int LevelsCompleted
    {
        get
        {
            ProfileManager pm = ProfileManager.Instance;
            return pm != null && pm.ActiveProfile != null ? pm.ActiveProfile.levelsCompleted : levelsCompletedWithoutProfile;
        }
    }

    void MarkLevelCompleted()
    {
        ProfileManager pm = ProfileManager.Instance;
        if (pm != null && pm.ActiveProfile != null)
        {
            pm.ActiveProfile.levelsCompleted = levelNumber;
            pm.SaveActiveProfile();
        }
        else
        {
            levelsCompletedWithoutProfile = levelNumber;
        }
    }

    void ChooseLevel()
    {
        int done = LevelsCompleted;
        levelNumber = done + 1;
        contractOffers = RickshawLevels.GenerateOffers(done);
        level = null;
    }

    void ShowLevelIntro()
    {
        OpenLevelPanel("CHOOSE CONTRACT  •  RANK " + levelNumber,
            "Pick a shift. Contracts become harder and more rewarding as your rank grows.\n" +
            "Night Rush starts after dark immediately, so you can play the night city now.");

        for (int i = 0; i < contractOffers.Length; i++)
        {
            int offerIndex = i;
            RickshawLevels.Level offer = contractOffers[i];
            string prefix = i == 0 ? "STANDARD  •  " : (i == 1 ? "PRECISION  •  " : "NIGHT  •  ");
            Color color = i == 0 ? new Color(0.2f, 0.65f, 0.3f, 1f)
                : (i == 1 ? new Color(0.88f, 0.58f, 0.12f, 1f) : new Color(0.25f, 0.32f, 0.7f, 1f));
            AddLevelButton(prefix + offer.title, color, () => SelectContract(offerIndex));
        }
    }

    void SelectContract(int offerIndex)
    {
        level = contractOffers[Mathf.Clamp(offerIndex, 0, contractOffers.Length - 1)];
        levelHud.SetActive(true);
        OpenLevelPanel(level.title.ToUpperInvariant() + "  •  RANK " + levelNumber,
            level.Rules() + "\n\n" + RewardText());
        AddLevelButton("START", new Color(0.2f, 0.65f, 0.3f, 1f), StartLevel);
        AddLevelButton("BACK", new Color(0.3f, 0.33f, 0.4f, 1f), ShowLevelIntro);
    }

    string RewardText()
    {
        RickshawSkins.Skin skin = RickshawSkins.UnlockedBy(levelNumber);
        return "Reward: bonus coins for your average rating" + (skin != null ? " + unlocks " + skin.name : "");
    }

    void StartLevel()
    {
        levelStartMoney = MoneyManager.Instance != null ? MoneyManager.Instance.CurrentMoney : 0;
        levelTimeLeft = level.timeLimit;
        contractCrashes = 0;
        contractViolations = 0;

        if (level.nightShift && DayNightSystem.Instance != null)
            DayNightSystem.Instance.AdvanceToHour(19.5f);

        if (level.lowFuelStart)
            foreach (RideVehicle vehicle in vehicles)
                if (vehicle.fuel != null)
                    vehicle.fuel.SetFuelFraction(0.35f);

        CityTraffic cityTraffic = FindAnyObjectByType<CityTraffic>();
        if (cityTraffic != null)
            cityTraffic.SetDifficulty(level.trafficSpeedMultiplier);

        levelRunning = true;
        levelPanelOpen = false;
        levelPanel.SetActive(false);
        Time.timeScale = 1f;
        Toast(level.nightShift ? "Night shift started — street lights are on." : level.title + " started.");
    }

    void OnTrafficFineCharged(int amount)
    {
        if (levelRunning)
            contractViolations++;
    }

    void TickLevel()
    {
        if (!levelRunning) return;
        levelTimeLeft -= Time.deltaTime;
        if (levelTimeLeft <= 0f)
            FailLevel("Time's up! You served " + RidesCompleted + " of " + level.customers + " customers.");
    }

    void FinishLevel()
    {
        if (level.noCrashes && contractCrashes > 0)
        {
            FailLevel("This contract required clean passenger rides, but you had " + contractCrashes +
                (contractCrashes == 1 ? " crash." : " crashes."));
            return;
        }
        if (level.noViolations && contractViolations > 0)
        {
            FailLevel("This contract allowed no traffic violations (red lights or speed radars). You received " + contractViolations +
                (contractViolations == 1 ? " fine." : " fines."));
            return;
        }

        float average = AverageStars;
        if (average < level.minAverageStars)
        {
            FailLevel("Your average rating was " + average.ToString("0.0") + " stars. You needed " + level.minAverageStars.ToString("0.#") + ".");
            return;
        }

        levelRunning = false;
        int bonus = Mathf.RoundToInt(average * RickshawLevels.BonusPerStar);
        if (MoneyManager.Instance != null)
            MoneyManager.Instance.AddMoney(bonus);
        MarkLevelCompleted();

        string body = "Average rating: " + average.ToString("0.0") + " stars\nLevel bonus: +" + bonus + " coins";
        RickshawSkins.Skin skin = RickshawSkins.UnlockedBy(levelNumber);
        if (skin != null)
            body += "\n\nNew skin in the Item Shop: " + skin.name;
        OpenLevelPanel("CONTRACT COMPLETE!", body);

        AddLevelButton("NEXT CONTRACT", new Color(0.2f, 0.65f, 0.3f, 1f), ReloadCity);
        AddLevelButton("ITEM SHOP", new Color(0.95f, 0.72f, 0.12f, 1f), () => ReturnToMenu(true));
        AddLevelButton("MAIN MENU", new Color(0.3f, 0.33f, 0.4f, 1f), () => ReturnToMenu(false));
    }

    void FailLevel(string reason)
    {
        levelRunning = false;
        RestoreLevelStartMoney();
        OpenLevelPanel("CONTRACT FAILED", reason);
        AddLevelButton("RETRY", new Color(0.2f, 0.65f, 0.3f, 1f), ReloadCity);
        AddLevelButton("MAIN MENU", new Color(0.3f, 0.33f, 0.4f, 1f), () => ReturnToMenu(false));
    }

    // also written to the profile directly: on a scene change MoneyManager may already be gone
    void RestoreLevelStartMoney()
    {
        if (MoneyManager.Instance != null)
            MoneyManager.Instance.RestoreBalance(levelStartMoney);

        ProfileManager pm = ProfileManager.Instance;
        if (pm != null && pm.ActiveProfile != null)
        {
            pm.ActiveProfile.money = levelStartMoney;
            pm.SaveActiveProfile();
        }
    }

    void ReloadCity()
    {
        Time.timeScale = 1f;
        SceneManager.LoadScene(gameObject.scene.name);
    }

    void OpenLevelPanel(string title, string body)
    {
        levelPanelOpen = true;
        Time.timeScale = 0f;
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        if (resultPanel != null) resultPanel.SetActive(false);

        foreach (GameObject b in levelButtons) Destroy(b);
        levelButtons.Clear();
        levelPanelTitle.text = title;
        levelPanelBody.text = body;
        levelPanel.SetActive(true);
    }

    void AddLevelButton(string label, Color color, UnityEngine.Events.UnityAction onClick)
    {
        Transform panel = levelPanel.transform;
        int before = panel.childCount;
        Vector2 pos = new Vector2(0f, -130f - levelButtons.Count * 115f);
        MakeMenuButton(panel, label, new Vector2(0.5f, 0.5f), pos, new Vector2(620f, 100f), color, levelFont, 42, onClick);
        levelButtons.Add(panel.GetChild(before).gameObject);
    }

    Font levelFont;

    void BuildLevelUi(Transform canvas, Font font)
    {
        levelFont = font;

        RectTransform hud = MakePanel(canvas, "LevelPanel", new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(360f, 190f));
        levelHudText = MakeText(hud, font, 30, FontStyle.Bold, TextAnchor.MiddleCenter, new Vector2(10f, 6f), new Vector2(-10f, -6f));
        levelHud = hud.gameObject;
        levelHud.SetActive(level != null);

        RectTransform dim = MakePanel(canvas, "LevelMessagePanel", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4000f, 3000f));
        Image dimImg = dim.GetComponent<Image>();
        dimImg.color = new Color(0f, 0f, 0f, 0.8f);
        dimImg.raycastTarget = true; // blocks clicks behind the panel

        RectTransform title = MakePanel(dim, "LevelTitle", new Vector2(0.5f, 0.5f), new Vector2(0f, 340f), new Vector2(1200f, 110f));
        title.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        levelPanelTitle = MakeText(title, font, 76, FontStyle.Bold, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero);

        RectTransform body = MakePanel(dim, "LevelBody", new Vector2(0.5f, 0.5f), new Vector2(0f, 120f), new Vector2(1200f, 240f));
        body.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        levelPanelBody = MakeText(body, font, 36, FontStyle.Normal, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero);

        levelPanel = dim.gameObject;
        levelPanel.SetActive(false);
    }

    void RefreshLevelHud()
    {
        if (level == null || levelHudText == null) return;

        float time = levelRunning ? levelTimeLeft : level.timeLimit;
        string timeColor = levelRunning && time <= 30f ? "#FF5555" : "#FFD54A";
        string text = level.title.ToUpperInvariant() + "  •  RANK " + levelNumber + "\n" +
            "<size=52><color=" + timeColor + ">" + FormatTime(time) + "</color></size>\n" +
            "Customers " + RidesCompleted + " / " + level.customers;
        if (level.minAverageStars > 0f)
            text += "\n<size=24>Rating " + (RidesCompleted > 0 ? AverageStars.ToString("0.0") : "-") + "  (need " + level.minAverageStars.ToString("0.#") + ")</size>";
        levelHudText.text = text;
    }

    [Header("Pause menu")]
    [SerializeField] string menuSceneName = "MainMenu";

    bool paused;
    GameObject pausePanel;

    // Esc (or the MENU button) pauses the game and offers: Resume / Item Shop / Main Menu.
    // Coins are already saved to the profile on every change, so nothing is lost when leaving.
    void SetPaused(bool value)
    {
        paused = value;
        if (pausePanel != null) pausePanel.SetActive(value);
        Time.timeScale = value ? 0f : 1f;
        AudioListener.pause = value;
        if (value)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
    }

    void ReturnToMenu(bool openShop)
    {
        Time.timeScale = 1f;
        AudioListener.pause = false;
        RickshawSkins.OpenShopOnLoad = openShop;
        SceneManager.LoadScene(menuSceneName);
    }

    void OnDestroy()
    {
        if (trafficLights != null)
            trafficLights.FineCharged -= OnTrafficFineCharged;
        SpeedRadar.AnyFineCharged -= OnTrafficFineCharged;

        // leaving in the middle of a level counts as restarting it
        if (levelRunning) RestoreLevelStartMoney();

        // never leave the game frozen if this scene is unloaded while paused
        Time.timeScale = 1f;
        AudioListener.pause = false;
        if (mapTexture != null) mapTexture.Release();
    }

    void BuildPauseUi(Transform canvas, Font font)
    {
        // small always-visible MENU button under the fuel readout (top-left)
        MakeMenuButton(canvas, "MENU  (Esc)", new Vector2(0f, 1f), new Vector2(24f, -212f), new Vector2(330f, 64f), new Color(0.2f, 0.35f, 0.6f, 0.95f), font, 30, () => SetPaused(true));

        RectTransform dim = MakePanel(canvas, "PausePanel", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4000f, 3000f));
        Image dimImg = dim.GetComponent<Image>();
        dimImg.color = new Color(0f, 0f, 0f, 0.75f);
        dimImg.raycastTarget = true; // blocks clicks behind the menu

        RectTransform title = MakePanel(dim, "PauseTitle", new Vector2(0.5f, 0.5f), new Vector2(0f, 270f), new Vector2(900f, 110f));
        title.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        Text titleText = MakeText(title, font, 76, FontStyle.Bold, TextAnchor.MiddleCenter, new Vector2(0f, 0f), new Vector2(0f, 0f));
        titleText.text = "PAUSED";

        MakeMenuButton(dim, "RESUME", new Vector2(0.5f, 0.5f), new Vector2(0f, 110f), new Vector2(620f, 100f), new Color(0.2f, 0.65f, 0.3f, 1f), font, 42, () => SetPaused(false));
        MakeMenuButton(dim, "ITEM SHOP", new Vector2(0.5f, 0.5f), new Vector2(0f, -10f), new Vector2(620f, 100f), new Color(0.95f, 0.72f, 0.12f, 1f), font, 42, () => ReturnToMenu(true));
        MakeMenuButton(dim, "MAIN MENU", new Vector2(0.5f, 0.5f), new Vector2(0f, -130f), new Vector2(620f, 100f), new Color(0.3f, 0.33f, 0.4f, 1f), font, 42, () => ReturnToMenu(false));

        RectTransform hint = MakePanel(dim, "PauseHint", new Vector2(0.5f, 0.5f), new Vector2(0f, -250f), new Vector2(1000f, 60f));
        hint.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        Text hintText = MakeText(hint, font, 28, FontStyle.Normal, TextAnchor.MiddleCenter, new Vector2(0f, 0f), new Vector2(0f, 0f));
        hintText.text = level != null
            ? "Leaving now restarts this level.   Esc = resume"
            : "Your coins and skins are saved automatically.   Esc = resume";

        pausePanel = dim.gameObject;
        pausePanel.SetActive(false);
    }

    static void MakeMenuButton(Transform parent, string label, Vector2 anchor, Vector2 pos, Vector2 size, Color color, Font font, int fontSize, UnityEngine.Events.UnityAction onClick)
    {
        RectTransform rt = MakePanel(parent, label + " Button", anchor, pos, size);
        Image img = rt.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = true;
        Button btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);
        Text t = MakeText(rt, font, fontSize, FontStyle.Bold, TextAnchor.MiddleCenter, new Vector2(4f, 2f), new Vector2(-4f, -2f));
        t.text = label;
    }

    // The skin equipped in the Main Menu item shop (saved in the active profile) is applied when the city loads.
    void ApplyEquippedSkin(RideVehicle v)
    {
        if (!v.useEquippedSkin || ProfileManager.Instance == null || ProfileManager.Instance.ActiveProfile == null)
            return;
        string id = ProfileManager.Instance.ActiveProfile.equippedSkin;
        if (string.IsNullOrEmpty(id))
            return;
        RickshawSkins.Apply(v.Transform, v.bodyMaterialName, RickshawSkins.Find(id));
    }

    [Header("Default seat values for auto-detected vehicles")]
    [SerializeField] Vector3 driverSeatOffset = Vector3.zero;
    [SerializeField] Vector3 passengerSeatOffset = Vector3.zero;
    [Header("Seat height above ground (lower = sits deeper in the rickshaw)")]
    [SerializeField] float seatHeight = 0.25f;

    [Header("Refuelling")]
    [Tooltip("Scene objects (by name) that are gas stations.")]
    [SerializeField] string[] fuelStationNames = { "Gas_station_A_PREFAB" };
    [Tooltip("Where the stall is, in each station object's local space.")]
    [SerializeField] Vector3 stallLocalOffset = new Vector3(5.3f, 0f, 12.9f);
    [SerializeField] float stallRadius = 3.5f;
    [SerializeField] int refuelCost = 10;
    [Tooltip("Refuelling only happens when the tank is below this fraction (0-1).")]
    [SerializeField, Range(0f, 1f)] float refuelBelowFraction = 0.95f;
    [Tooltip("Show the stall marker all the time while riding, not just when fuel is low.")]
    [SerializeField] bool alwaysShowStallMarker = false;

    [Header("Camera")]
    [SerializeField] float cameraDistance = 7f;
    [SerializeField] float cameraPitch = 18f;
    [SerializeField] float orbitSensitivity = 0.15f;

    GameObject driver;
    CharacterController driverController;
    Animator driverAnimator;
    static readonly int WalkingHash = Animator.StringToHash("Walking");
    // Walking.anim is an in-place cycle of about 1.47 m/s. Playback is scaled to walkSpeed so the feet keep up.
    const float WalkClipMetersPerSecond = 1.47f;
    GameObject customer;
    Camera rideCamera;

    RideVehicle current; // the vehicle being ridden (null = on foot)
    bool started;
    readonly List<Landmark> landmarks = new List<Landmark>();
    readonly List<FuelStall> stalls = new List<FuelStall>();

    bool hasPassenger;
    string customerLabel = "the bus stop";
    string destinationLabel = "";
    Vector3 destination;
    Vector3 tripStart;
    string lastPickupLabel = "";
    float camYaw;
    bool cameraSnapped;
    string toast = "";
    float toastUntil;
    float nextRefuelCheck;

    Text guidanceText, toastText, fuelWarnText;
    GameObject toastBox, fuelWarnBox, controlsPanel;
    Image fuelWarnImage;
    static bool controlsSeen; // reloading the city for a retry or the next level doesn't show them again
    float controlsHideAt = -1f;
    const float controlsShowTime = 3f;
    Text coinText, fuelText, worldClockText, speedText;
    GameObject speedPanel;
    float shownSpeedKmh;
    RectTransform coinPanel, coinDeductionRt;
    Text coinDeductionText;
    Vector2 coinPanelHome;
    float coinDeductionStarted = -1f;
    const float CoinDeductionDuration = 1.1f;
    RectTransform fuelFill;
    Image fuelFillImage;

    // Always-visible coin + fuel readouts (top-left), drawn by our own camera so nothing can cover them.
    void BuildHud(Transform canvas, Font font)
    {
        RectTransform coin = MakePanel(canvas, "CoinsPanel", new Vector2(0f, 1f), new Vector2(24f, -24f), new Vector2(330f, 74f));
        coinPanel = coin;
        coinPanelHome = coin.anchoredPosition;
        coinText = MakeText(coin, font, 40, FontStyle.Bold, TextAnchor.MiddleCenter, new Vector2(8f, 4f), new Vector2(-8f, -4f));

        GameObject deduction = new GameObject("CoinDeduction", typeof(RectTransform), typeof(Text));
        deduction.transform.SetParent(coin, false);
        coinDeductionRt = (RectTransform)deduction.transform;
        coinDeductionRt.anchorMin = new Vector2(0.5f, 0f);
        coinDeductionRt.anchorMax = new Vector2(0.5f, 0f);
        coinDeductionRt.pivot = new Vector2(0.5f, 1f);
        coinDeductionRt.anchoredPosition = new Vector2(0f, -8f);
        coinDeductionRt.sizeDelta = new Vector2(300f, 64f);
        coinDeductionText = deduction.GetComponent<Text>();
        coinDeductionText.font = font;
        coinDeductionText.fontSize = 38;
        coinDeductionText.fontStyle = FontStyle.Bold;
        coinDeductionText.alignment = TextAnchor.MiddleCenter;
        coinDeductionText.color = new Color(1f, 0.18f, 0.12f, 1f);
        coinDeductionText.raycastTarget = false;
        deduction.SetActive(false);

        RectTransform fuelPanel = MakePanel(canvas, "FuelReadoutPanel", new Vector2(0f, 1f), new Vector2(24f, -110f), new Vector2(330f, 90f));
        fuelText = MakeText(fuelPanel, font, 28, FontStyle.Bold, TextAnchor.UpperLeft, new Vector2(14f, 4f), new Vector2(-14f, -6f));

        GameObject back = new GameObject("BarBack", typeof(RectTransform), typeof(Image));
        back.transform.SetParent(fuelPanel, false);
        RectTransform backRt = (RectTransform)back.transform;
        backRt.anchorMin = new Vector2(0f, 0f);
        backRt.anchorMax = new Vector2(1f, 0f);
        backRt.pivot = new Vector2(0.5f, 0f);
        backRt.offsetMin = new Vector2(14f, 12f);
        backRt.offsetMax = new Vector2(-14f, 30f);
        Image backImg = back.GetComponent<Image>();
        backImg.color = new Color(1f, 1f, 1f, 0.18f);
        backImg.raycastTarget = false;

        GameObject fill = new GameObject("BarFill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(back.transform, false);
        fuelFill = (RectTransform)fill.transform;
        fuelFill.anchorMin = Vector2.zero;
        fuelFill.anchorMax = Vector2.one;
        fuelFill.offsetMin = Vector2.zero;
        fuelFill.offsetMax = Vector2.zero;
        fuelFillImage = fill.GetComponent<Image>();
        fuelFillImage.raycastTarget = false;

        RectTransform speedRt = MakePanel(canvas, "SpeedometerPanel", new Vector2(0f, 1f), new Vector2(24f, -356f), new Vector2(330f, 70f));
        speedText = MakeText(speedRt, font, 34, FontStyle.Bold, TextAnchor.MiddleCenter, new Vector2(8f, 4f), new Vector2(-8f, -4f));
        speedPanel = speedRt.gameObject;
        speedPanel.SetActive(false); // shown only while driving

        RectTransform clockPanel = MakePanel(canvas, "WorldClockPanel", new Vector2(0f, 1f), new Vector2(24f, -286f), new Vector2(330f, 60f));
        worldClockText = MakeText(clockPanel, font, 27, FontStyle.Bold, TextAnchor.MiddleCenter, new Vector2(8f, 3f), new Vector2(-8f, -3f));
    }

    void RefreshHud()
    {
        if (coinText == null) return;

        int money = MoneyManager.Instance != null ? MoneyManager.Instance.CurrentMoney : 0;
        bool showingDeduction = coinDeductionStarted >= 0f;
        coinText.text = "<color=" + (showingDeduction ? "#FF4D42" : "#FFD54A") + ">$ " + money.ToString("N0") + "</color>";
        UpdateCoinDeductionAnimation();
        if (worldClockText != null)
            worldClockText.text = DayNightSystem.Instance != null ? DayNightSystem.Instance.FormattedTime : "DAY 1";
        UpdateSpeedometer();

        TukTukFuelSystem f = null;
        if (current != null)
        {
            f = current.fuel;
        }
        else
        {
            float d;
            RideVehicle nearest = NearestVehicle(driver.transform.position, out d);
            if (nearest != null) f = nearest.fuel;
        }

        if (f == null)
        {
            fuelText.text = "FUEL  --";
            fuelFill.anchorMax = new Vector2(0f, 1f);
            return;
        }

        float n = f.NormalizedFuel;
        bool low = f.IsLow || f.IsEmpty;
        Color c = low ? new Color(1f, 0.22f, 0.22f, 1f) : new Color(0.96f, 0.77f, 0.24f, 1f);
        fuelFillImage.color = c;
        fuelFill.anchorMax = new Vector2(Mathf.Clamp01(n), 1f);
        fuelText.text = "FUEL  " + Mathf.RoundToInt(n * 100f) + "%" + (f.IsEmpty ? "  EMPTY" : (low ? "  LOW" : ""));
        fuelText.color = low ? c : Color.white;
    }

    void UpdateSpeedometer()
    {
        if (speedPanel == null) return;

        TukTukController car = current != null ? current.driveController as TukTukController : null;
        if (car == null)
        {
            if (speedPanel.activeSelf) speedPanel.SetActive(false);
            shownSpeedKmh = 0f;
            return;
        }
        if (!speedPanel.activeSelf) speedPanel.SetActive(true);

        // smooth the reading so small physics jitter does not make the number flicker
        shownSpeedKmh = Mathf.Lerp(shownSpeedKmh, car.SpeedKmh, 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime));
        int kmh = Mathf.RoundToInt(shownSpeedKmh);

        const float radarWarningRange = 60f;
        int limit;
        bool radarAhead = SpeedRadar.TryGetLimitAhead(car.transform.position, FlatForward(car.transform), radarWarningRange, out limit);
        bool over = radarAhead && kmh > limit;
        string colour = over ? "#FF4D42" : (radarAhead ? "#FFD54A" : "#FFFFFF");
        speedText.text = "<size=24>SPEED</size>  <color=" + colour + ">" + kmh + " km/h</color>" +
            (radarAhead ? "  <size=24><color=#FFD54A>RADAR " + limit + "</color></size>" : "");
    }

    /// <summary>The vehicle the player is driving, or null while on foot.</summary>
    public Transform DrivenVehicle { get { return current != null ? current.Transform : null; } }

    /// <summary>
    /// Where the player should go next: the waiting customer (isPickup) or the passenger's
    /// drop-off point. Used by RideNavigator for the floating marker and the road arrows.
    /// </summary>
    public bool TryGetNavigationTarget(out Vector3 target, out bool isPickup)
    {
        target = Vector3.zero;
        isPickup = false;
        if (!started || paused) return false;
        if (hasPassenger)
        {
            target = destination;
            return true;
        }
        if (customer != null)
        {
            target = customer.transform.position;
            isPickup = true;
            return true;
        }
        return false;
    }

    /// <summary>Shows a short message in the toast bar (used by other systems such as speed radars).</summary>
    public void ShowNotice(string message)
    {
        Toast(message);
    }

    /// <summary>Plays visible feedback on the gameplay coin HUD when coins are taken.</summary>
    public void ShowCoinDeduction(int amount)
    {
        if (amount <= 0 || coinPanel == null || coinDeductionText == null)
            return;

        coinDeductionText.text = "-$" + amount.ToString("N0");
        coinDeductionText.gameObject.SetActive(true);
        coinDeductionStarted = Time.unscaledTime;
        UpdateCoinDeductionAnimation();
    }

    void UpdateCoinDeductionAnimation()
    {
        if (coinDeductionStarted < 0f || coinPanel == null || coinDeductionText == null)
            return;

        float progress = (Time.unscaledTime - coinDeductionStarted) / CoinDeductionDuration;
        if (progress >= 1f)
        {
            coinDeductionStarted = -1f;
            coinPanel.anchoredPosition = coinPanelHome;
            coinPanel.localScale = Vector3.one;
            coinDeductionText.gameObject.SetActive(false);
            return;
        }

        progress = Mathf.Clamp01(progress);
        float fade = 1f - Mathf.SmoothStep(0.65f, 1f, progress);
        float shake = Mathf.Sin(progress * Mathf.PI * 12f) * (1f - progress) * 12f;
        float punch = 1f + Mathf.Sin(progress * Mathf.PI) * 0.18f;

        coinPanel.anchoredPosition = coinPanelHome + new Vector2(shake, 0f);
        coinPanel.localScale = Vector3.one * punch;
        coinDeductionRt.anchoredPosition = new Vector2(0f, -8f - 34f * progress);
        coinDeductionText.color = new Color(1f, 0.18f, 0.12f, fade);
    }
    GameObject stallMarker;
    Transform stallArrow;

    // ---------- public API for future vehicles ----------

    /// <summary>Makes any vehicle rideable. driveController is the Behaviour that moves it from input.</summary>
    public RideVehicle RegisterVehicle(Behaviour driveController, string displayName)
    {
        if (driveController == null) return null;
        foreach (RideVehicle existing in vehicles)
            if (existing.driveController == driveController) return existing;

        RideVehicle v = NewVehicle(driveController, displayName);
        vehicles.Add(v);
        if (started) PrepareVehicle(v);
        return v;
    }

    RideVehicle NewVehicle(Behaviour controller, string displayName)
    {
        RideVehicle v = new RideVehicle();
        v.displayName = displayName;
        v.driveController = controller;
        v.seatHeight = seatHeight;
        v.driverSeatOffset = driverSeatOffset;
        v.passengerSeatOffset = passengerSeatOffset;
        return v;
    }

    // ---------- setup ----------

    void Start()
    {
        if (vehicles.Count == 0)
        {
            foreach (TukTukController c in FindObjectsByType<TukTukController>(FindObjectsSortMode.None))
                vehicles.Add(NewVehicle(c, "Rickshaw"));
        }

        for (int i = vehicles.Count - 1; i >= 0; i--)
        {
            if (vehicles[i].driveController == null) { vehicles.RemoveAt(i); continue; }
            PrepareVehicle(vehicles[i]);
            ApplyEquippedSkin(vehicles[i]);
        }

        if (vehicles.Count == 0 || driverPrefab == null || customerPrefab == null)
        {
            Debug.LogError("RickshawRideSystem: no rideable vehicle or missing prefab references. Disabling.");
            enabled = false;
            return;
        }

        started = true;
        ChooseLevel();
        trafficLights = FindAnyObjectByType<TrafficLightSystem>();
        if (trafficLights != null)
            trafficLights.FineCharged += OnTrafficFineCharged;
        SpeedRadar.AnyFineCharged += OnTrafficFineCharged;
        ResolveLandmarks();
        ResolveFuelStalls();
        BuildMapTexture(); // before the driver/customer exist, so they are not baked into the map picture
        SpawnDriver();
        SpawnFirstCustomer();
        CreateCamera();
        BuildUi();
        BuildStallMarker();
        if (GetComponent<RideNavigator>() == null) gameObject.AddComponent<RideNavigator>();
        ShowLevelIntro();
    }

    void PrepareVehicle(RideVehicle v)
    {
        if (v.fuel == null) v.fuel = v.driveController.GetComponentInParent<TukTukFuelSystem>();
        if (v.body == null) v.body = v.driveController.GetComponentInParent<Rigidbody>();
        v.driveController.enabled = false; // a vehicle only drives while it is being ridden
        v.lastSafePos = v.driveController.transform.position + Vector3.up * 0.3f;
        v.lastSafeRot = Quaternion.LookRotation(FlatForward(v.driveController.transform));
        ComputeSeats(v);
    }

    void ResolveLandmarks()
    {
        for (int i = 0; i < landmarkNames.Length; i++)
        {
            GameObject found = GameObject.Find(landmarkNames[i]);
            if (found == null)
                continue;
            Landmark l;
            l.label = i < landmarkLabels.Length ? landmarkLabels[i] : landmarkNames[i];
            l.position = found.transform.position;
            landmarks.Add(l);
        }
    }

    void ResolveFuelStalls()
    {
        foreach (string stationName in fuelStationNames)
        {
            GameObject station = GameObject.Find(stationName);
            if (station == null)
                continue;
            FuelStall s;
            s.label = "the gas station";
            Vector3 p = station.transform.TransformPoint(stallLocalOffset);
            p.y = station.transform.position.y; // ground level of the station
            s.position = p;
            stalls.Add(s);
        }
    }

    void ComputeSeats(RideVehicle v)
    {
        Transform vt = v.Transform;
        Bounds bounds = new Bounds(vt.position, Vector3.zero);
        bool any = false;
        foreach (Renderer r in vt.GetComponentsInChildren<Renderer>())
        {
            if (!any) { bounds = r.bounds; any = true; }
            else bounds.Encapsulate(r.bounds);
        }

        Vector3 f = FlatForward(vt);
        float halfLength = Mathf.Abs(f.x) * bounds.extents.x + Mathf.Abs(f.z) * bounds.extents.z;
        Vector3 baseSeat = new Vector3(bounds.center.x, bounds.min.y + v.seatHeight, bounds.center.z);
        v.driverSeatLocal = vt.InverseTransformPoint(baseSeat + f * (halfLength * 0.25f)) + v.driverSeatOffset;
        v.passengerSeatLocal = vt.InverseTransformPoint(baseSeat - f * (halfLength * 0.35f)) + v.passengerSeatOffset;
    }

    void SpawnDriver()
    {
        Transform vt = vehicles[0].Transform;
        Vector3 right = Vector3.Cross(Vector3.up, FlatForward(vt));
        Vector3 pos = GroundPoint(vt.position + right * 3f);
        driver = Instantiate(driverPrefab, pos, Quaternion.LookRotation(FlatForward(vt)), transform);
        driver.name = "Driver (Character 01)";
        driverController = driver.AddComponent<CharacterController>();
        driverController.height = 1.8f;
        driverController.radius = 0.3f;
        driverController.center = new Vector3(0f, 0.9f, 0f);
        driverController.stepOffset = 0.3f;
        driverAnimator = driver.GetComponent<Animator>();
        if (driverAnimator != null)
            driverAnimator.applyRootMotion = false; // CharacterController moves him; the clip only poses the body
        camYaw = driver.transform.eulerAngles.y;
        driverSafePos = driver.transform.position;
    }

    void SpawnFirstCustomer()
    {
        Vector3 vehiclePos = vehicles[0].Transform.position;
        GameObject stop = GameObject.Find(busStopName);
        Vector3 stopPos = stop != null ? stop.transform.position : vehiclePos + FlatForward(vehicles[0].Transform) * 40f;
        Vector3 towardVehicle = vehiclePos - stopPos;
        towardVehicle.y = 0f;
        Vector3 spot = stopPos + (towardVehicle.sqrMagnitude > 0.01f ? towardVehicle.normalized * 2.5f : Vector3.right * 2.5f);
        SpawnCustomerAt(spot, "the bus stop");
    }

    void SpawnCustomerAt(Vector3 spot, string label)
    {
        customer = Instantiate(customerPrefab, GroundPoint(spot), Quaternion.identity, transform);
        customer.name = "Customer (Character 02)";
        Vector3 look = ReferencePosition() - customer.transform.position;
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f)
            customer.transform.rotation = Quaternion.LookRotation(look);
        customerLabel = label;
        lastPickupLabel = label;
    }

    void CreateCamera()
    {
        GameObject camObj = new GameObject("RideCamera");
        camObj.transform.SetParent(transform, false);
        rideCamera = camObj.AddComponent<Camera>();
        rideCamera.depth = 1000f; // draws above existing cameras; the existing ones are not touched
        rideCamera.fieldOfView = 60f;
        rideCamera.nearClipPlane = 0.3f;
        rideCamera.farClipPlane = 1500f;
    }

    // ---------- per frame ----------

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null || driver == null || levelPanelOpen)
            return;

        if (kb.escapeKey.wasPressedThisFrame)
            SetPaused(!paused);
        if (paused)
            return;

        TickLevel();
        if (levelPanelOpen)
            return;

        if (kb.mKey.wasPressedThisFrame)
            mapZoomIndex = (mapZoomIndex + 1) % mapZoomRadii.Length;

        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            ToggleRide();

        if (kb.pKey.wasPressedThisFrame)
            HandleP();

        if (current == null)
            Walk(kb);
        else
            TryRefuel();
    }

    void FixedUpdate()
    {
        DetectCrash();
        CheckFalls();

        // parked vehicles: bleed off horizontal speed (their physics material has zero friction)
        foreach (RideVehicle v in vehicles)
        {
            if (v == current || v.body == null) continue;
            Vector3 vel = v.body.linearVelocity;
            Vector3 flat = Vector3.MoveTowards(new Vector3(vel.x, 0f, vel.z), Vector3.zero, 25f * Time.fixedDeltaTime);
            v.body.linearVelocity = new Vector3(flat.x, vel.y, flat.z);
        }
    }

    void LateUpdate()
    {
        if (driver == null)
            return;

        if (current != null || paused || levelPanelOpen)
            SetWalking(false);

        if (current != null)
        {
            Transform vt = current.Transform;
            Quaternion rot = Quaternion.LookRotation(FlatForward(vt));
            driver.transform.SetPositionAndRotation(vt.TransformPoint(current.driverSeatLocal), rot);
            if (hasPassenger && customer != null)
                customer.transform.SetPositionAndRotation(vt.TransformPoint(current.passengerSeatLocal), rot);
        }

        UpdateCamera();
        RefreshUi();
    }

    // ---------- on foot ----------

    void Walk(Keyboard kb)
    {
        float x = 0f, y = 0f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;

        Vector3 dir = Quaternion.Euler(0f, camYaw, 0f) * new Vector3(x, 0f, y);
        if (dir.sqrMagnitude > 1f) dir.Normalize();

        bool moving = dir.sqrMagnitude > 0.01f && driverController.enabled;
        if (driverController.enabled)
            driverController.SimpleMove(dir * walkSpeed);

        if (moving)
        {
            Quaternion target = Quaternion.LookRotation(dir);
            driver.transform.rotation = Quaternion.Slerp(driver.transform.rotation, target, 1f - Mathf.Exp(-turnSpeed * Time.deltaTime));
        }

        SetWalking(moving);
    }

    void SetWalking(bool moving)
    {
        if (driverAnimator == null)
            return;
        driverAnimator.SetBool(WalkingHash, moving);
        if (!moving)
        {
            driverAnimator.speed = 1f;
            return;
        }
        float rate = walkSpeed / WalkClipMetersPerSecond;
        if (rate < 0.6f) rate = 0.6f;
        if (rate > 3.2f) rate = 3.2f;
        driverAnimator.speed = rate;
    }

    // ---------- ride / leave ----------

    void ToggleRide()
    {
        if (current == null)
        {
            float dist;
            RideVehicle v = NearestVehicle(driver.transform.position, out dist);
            if (v == null || dist > boardRange)
            {
                Toast("Walk closer to a vehicle, then press Enter");
                return;
            }
            current = v;
            driverController.enabled = false;
            v.driveController.enabled = true;
            cameraSnapped = false;
            Toast("Riding the " + v.displayName.ToLower() + ". Drive with WASD / arrows.");
        }
        else
        {
            if (hasPassenger)
            {
                Toast("Drop off your customer first (P)");
                return;
            }
            if (VehicleSpeed(current) > 1.5f)
            {
                Toast("Stop the vehicle before getting off");
                return;
            }
            Transform vt = current.Transform;
            current.driveController.enabled = false;
            Vector3 right = Vector3.Cross(Vector3.up, FlatForward(vt));
            driver.transform.position = GroundPoint(vt.position + right * 2.5f);
            driverController.enabled = true;
            current = null;
            Toast("On foot");
        }
    }

    // ---------- pick up / drop off ----------

    void HandleP()
    {
        if (current == null)
        {
            Toast("Get in a vehicle first (Enter)");
            return;
        }
        if (VehicleSpeed(current) > maxActionSpeed)
        {
            Toast("Slow down / stop first");
            return;
        }

        Vector3 vpos = current.Transform.position;
        if (!hasPassenger)
        {
            if (customer == null) return;
            if (FlatDistance(vpos, customer.transform.position) > pickupRange)
            {
                Toast("Drive closer to the customer");
                return;
            }
            PickUp();
        }
        else
        {
            if (FlatDistance(vpos, destination) > dropRange)
            {
                Toast("Not at the drop-off yet");
                return;
            }
            DropOff();
        }
    }

    void PickUp()
    {
        tripStart = customer.transform.position;
        ChooseDestination();
        hasPassenger = true;
        BeginRide();
        Transform vt = current.Transform;
        customer.transform.SetPositionAndRotation(
            vt.TransformPoint(current.passengerSeatLocal),
            Quaternion.LookRotation(FlatForward(vt)));
        Toast("Customer aboard. Take them to " + destinationLabel);
    }

    void ChooseDestination()
    {
        List<Landmark> options = new List<Landmark>();
        foreach (Landmark l in landmarks)
            if (FlatDistance(l.position, tripStart) >= minTripDistance && NearRoad(l.position))
                options.Add(l);

        if (options.Count > 0)
        {
            Landmark pick = options[Random.Range(0, options.Count)];
            destination = Roadside(pick.position);
            destinationLabel = pick.label;
            return;
        }

        // fallback: farthest known landmark, or straight ahead
        float best = -1f;
        destination = tripStart + FlatForward(current.Transform) * minTripDistance;
        destinationLabel = "the drop-off point";
        foreach (Landmark l in landmarks)
        {
            float d = FlatDistance(l.position, tripStart);
            if (d > best) { best = d; destination = l.position; destinationLabel = l.label; }
        }
        destination = Roadside(destination);
    }

    // ---------- road helpers (drop-offs happen at the kerb, not inside parks or behind fences) ----------

    CityTraffic roadMap;

    bool TryRoadPoint(Vector3 p, out Vector3 roadPoint)
    {
        roadPoint = p;
        if (roadMap == null) roadMap = FindAnyObjectByType<CityTraffic>();
        return roadMap != null && roadMap.TryGetNearestRoadPoint(p, out roadPoint);
    }

    bool NearRoad(Vector3 p)
    {
        Vector3 r;
        return !TryRoadPoint(p, out r) || FlatDistance(r, p) <= 25f; // no road data: accept everything
    }

    Vector3 Roadside(Vector3 p)
    {
        Vector3 r;
        return TryRoadPoint(p, out r) ? r : p;
    }

    void DropOff()
    {
        float multiplier = levelRunning && level != null ? level.fareMultiplier : 1f;
        int fare = Mathf.Max(1, Mathf.RoundToInt(rideFare * multiplier));
        RideResult result = ScoreRide(fare);
        if (levelRunning)
            contractCrashes += result.crashes;
        if (MoneyManager.Instance != null)
            MoneyManager.Instance.AddMoney(result.fare + result.tip);
        ShowRideResult(result);

        Transform vt = current.Transform;
        Vector3 right = Vector3.Cross(Vector3.up, FlatForward(vt));
        GameObject leaving = customer;
        leaving.transform.position = GroundPoint(vt.position + right * 2.5f);
        Destroy(leaving, 4f);

        customer = null;
        hasPassenger = false;
        Toast("Dropped off at " + destinationLabel + ".  +" + (result.fare + result.tip) + " coins");

        if (levelRunning && RidesCompleted >= level.customers)
            FinishLevel();
        else
            StartCoroutine(SpawnNextCustomer());
    }

    IEnumerator SpawnNextCustomer()
    {
        yield return new WaitForSeconds(3f);
        if (landmarks.Count == 0) yield break;

        Vector3 reference = ReferencePosition();
        List<Landmark> options = new List<Landmark>();
        foreach (Landmark l in landmarks)
            if (l.label != lastPickupLabel && l.label != destinationLabel
                && FlatDistance(l.position, reference) >= 40f)
                options.Add(l);
        if (options.Count == 0) options = landmarks;

        Landmark next = options[Random.Range(0, options.Count)];
        SpawnCustomerAt(SpotBeside(next.position), next.label);
        Toast("New customer waiting at " + next.label);
    }

    // a side of the landmark that is open ground, not on top of whatever stands next to it (the ATM backs onto the gas station)
    Vector3 SpotBeside(Vector3 landmark)
    {
        foreach (Vector3 side in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
        {
            Vector3 spot = landmark + side * 2f;
            if (GroundPoint(spot).y <= landmark.y + 0.3f) return spot;
        }
        return landmark + Vector3.right * 2f;
    }

    // ---------- refuelling ----------

    bool IsLowOnFuel()
    {
        return current != null && current.fuel != null && (current.fuel.IsLow || current.fuel.IsEmpty);
    }

    void TryRefuel()
    {
        if (current.fuel == null || stalls.Count == 0 || Time.time < nextRefuelCheck)
            return;
        if (current.fuel.NormalizedFuel >= refuelBelowFraction)
            return;

        Vector3 vpos = current.Transform.position;
        int index = NearestStall(vpos);
        if (FlatDistance(vpos, stalls[index].position) > stallRadius || VehicleSpeed(current) > maxActionSpeed)
            return;

        if (MoneyManager.Instance != null && refuelCost > 0 && !MoneyManager.Instance.SpendMoney(refuelCost))
        {
            Toast("Not enough money to refuel (" + refuelCost + " needed)");
            nextRefuelCheck = Time.time + 3f;
            return;
        }

        current.fuel.Refuel(current.fuel.MaxFuel);
        nextRefuelCheck = Time.time + 1f;
        Toast("Refuelled!  -" + refuelCost + " coins");
    }

    int NearestStall(Vector3 from)
    {
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < stalls.Count; i++)
        {
            float d = FlatDistance(from, stalls[i].position);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    void BuildStallMarker()
    {
        stallMarker = new GameObject("FuelStallMarker");
        stallMarker.transform.SetParent(transform, false);

        Material ringMat = MakeOverlayMaterial(new Color(0.2f, 1f, 0.35f, 0.4f));
        Material arrowMat = MakeOverlayMaterial(new Color(0.2f, 1f, 0.35f, 0.95f));

        GameObject ring = MakePrimitive(PrimitiveType.Cylinder, stallMarker.transform, ringMat);
        ring.name = "Ring";
        ring.transform.localScale = new Vector3(stallRadius * 2f, 0.02f, stallRadius * 2f);
        ring.transform.localPosition = new Vector3(0f, 0.1f, 0f);

        GameObject arrow = new GameObject("Arrow");
        arrow.transform.SetParent(stallMarker.transform, false);
        stallArrow = arrow.transform;

        GameObject shaft = MakePrimitive(PrimitiveType.Cylinder, stallArrow, arrowMat);
        shaft.name = "Shaft";
        shaft.transform.localScale = new Vector3(0.4f, 0.6f, 0.4f);
        shaft.transform.localPosition = new Vector3(0f, 1.8f, 0f);

        GameObject head = new GameObject("Head");
        head.transform.SetParent(stallArrow, false);
        head.AddComponent<MeshFilter>().sharedMesh = BuildCone(0.75f, 1.2f, 24);
        head.AddComponent<MeshRenderer>().sharedMaterial = arrowMat;

        stallMarker.SetActive(false);
    }

    static GameObject MakePrimitive(PrimitiveType type, Transform parent, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Collider col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        go.transform.SetParent(parent, false);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        // the overlay shader multiplies by vertex colour, so give the mesh white vertex colours
        Mesh mesh = go.GetComponent<MeshFilter>().mesh;
        Color[] colors = new Color[mesh.vertexCount];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        mesh.colors = colors;
        return go;
    }

    static Material MakeOverlayMaterial(Color color)
    {
        Shader shader = Shader.Find("UI/Default");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        Material m = new Material(shader);
        m.color = color;
        m.SetInt("unity_GUIZTestMode", 8); // Always: stays visible through the canopy roof
        m.renderQueue = 3500;
        return m;
    }

    // cone with its tip at the origin, widening upward; used as a downward-pointing arrow head
    static Mesh BuildCone(float radius, float height, int segments)
    {
        List<Vector3> verts = new List<Vector3>();
        List<Color> colors = new List<Color>();
        List<int> tris = new List<int>();
        verts.Add(Vector3.zero);
        verts.Add(new Vector3(0f, height, 0f));
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
        for (int i = 0; i < verts.Count; i++) colors.Add(Color.white);
        Mesh mesh = new Mesh();
        mesh.SetVertices(verts);
        mesh.SetColors(colors);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    void UpdateStallMarker(bool lowFuel)
    {
        if (stallMarker == null) return;
        bool show = current != null && stalls.Count > 0 && (lowFuel || alwaysShowStallMarker);
        if (stallMarker.activeSelf != show)
            stallMarker.SetActive(show);
        if (!show) return;

        int index = NearestStall(current.Transform.position);
        stallMarker.transform.position = stalls[index].position;
        stallArrow.localPosition = new Vector3(0f, 2.4f + Mathf.Sin(Time.time * 3f) * 0.35f, 0f);
        stallArrow.Rotate(0f, 90f * Time.deltaTime, 0f, Space.Self);
    }

    // ---------- camera ----------

    void UpdateCamera()
    {
        if (rideCamera == null) return;
        float dt = Time.deltaTime;

        Transform focus = current != null ? current.Transform : driver.transform;
        if (current != null)
        {
            Vector3 f = FlatForward(current.Transform);
            float targetYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            camYaw = cameraSnapped ? Mathf.LerpAngle(camYaw, targetYaw, 1f - Mathf.Exp(-3f * dt)) : targetYaw;
        }
        else if (Mouse.current != null && Mouse.current.rightButton.isPressed)
        {
            camYaw += Mouse.current.delta.ReadValue().x * orbitSensitivity;
        }

        Vector3 pivot = focus.position + Vector3.up * (current != null ? 1.4f : 1.5f);
        Quaternion rot = Quaternion.Euler(cameraPitch, camYaw, 0f);
        Vector3 desired = pivot - rot * Vector3.forward * cameraDistance;

        Transform ct = rideCamera.transform;
        if (!cameraSnapped)
        {
            ct.position = desired;
            cameraSnapped = true;
        }
        else
        {
            ct.position = Vector3.Lerp(ct.position, desired, 1f - Mathf.Exp(-8f * dt));
        }
        ct.rotation = Quaternion.LookRotation(pivot - ct.position);
    }

    // ---------- helpers ----------

    static Vector3 FlatForward(Transform t)
    {
        Vector3 f = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        return f.sqrMagnitude < 0.0001f ? Vector3.forward : f.normalized;
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }

    static float VehicleSpeed(RideVehicle v)
    {
        if (v == null || v.body == null) return 0f;
        Vector3 vel = v.body.linearVelocity;
        return new Vector3(vel.x, 0f, vel.z).magnitude;
    }

    RideVehicle NearestVehicle(Vector3 from, out float distance)
    {
        RideVehicle best = null;
        distance = float.MaxValue;
        foreach (RideVehicle v in vehicles)
        {
            if (v.Transform == null) continue;
            float d = FlatDistance(from, v.Transform.position);
            if (d < distance) { distance = d; best = v; }
        }
        return best;
    }

    Vector3 ReferencePosition()
    {
        if (current != null) return current.Transform.position;
        if (driver != null) return driver.transform.position;
        return vehicles[0].Transform.position;
    }

    Vector3 GroundPoint(Vector3 p)
    {
        Vector3 origin = p + Vector3.up * 6f;
        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 40f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit h in hits)
        {
            if (h.transform.IsChildOf(transform)) continue;
            bool onVehicle = false;
            foreach (RideVehicle v in vehicles)
                if (v.Transform != null && h.transform.root == v.Transform.root) { onVehicle = true; break; }
            if (onVehicle) continue;
            return h.point;
        }
        return p;
    }

    void Toast(string message)
    {
        toast = message;
        toastUntil = Time.time + 4f;
    }

    string Direction(Vector3 from, Vector3 to, Vector3 forward)
    {
        Vector3 delta = to - from;
        delta.y = 0f;
        if (delta.sqrMagnitude < 0.01f) return "Here";
        float angle = Vector3.SignedAngle(forward, delta, Vector3.up); // + = right
        float abs = Mathf.Abs(angle);
        if (abs <= 30f) return "Go forward";
        if (abs >= 150f) return "Go back";
        return angle > 0f ? "Go right" : "Go left";
    }

    // ---------- on-screen UI (uGUI, built at runtime under this object) ----------

    void BuildUi()
    {
        GameObject canvasObj = new GameObject("RideUI");
        canvasObj.transform.SetParent(transform, false);
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera; // drawn by our own camera so it can't be hidden
        canvas.worldCamera = rideCamera;
        canvas.planeDistance = 1f;
        canvas.sortingOrder = 5000;
        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasObj.AddComponent<GraphicRaycaster>(); // lets the pause menu be clicked with the mouse

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        RectTransform top = MakePanel(canvasObj.transform, "GuidancePanel", new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(1000f, 190f));
        guidanceText = MakeText(top, font, 30, FontStyle.Normal, TextAnchor.MiddleCenter, new Vector2(12f, 8f), new Vector2(-12f, -8f));

        RectTransform fuelWarn = MakePanel(canvasObj.transform, "FuelWarningPanel", new Vector2(0.5f, 1f), new Vector2(0f, -226f), new Vector2(1000f, 84f));
        fuelWarnImage = fuelWarn.GetComponent<Image>();
        fuelWarnText = MakeText(fuelWarn, font, 32, FontStyle.Bold, TextAnchor.MiddleCenter, new Vector2(12f, 4f), new Vector2(-12f, -4f));
        fuelWarnBox = fuelWarn.gameObject;
        fuelWarnBox.SetActive(false);

        BuildHud(canvasObj.transform, font);

        RectTransform help = MakePanel(canvasObj.transform, "ControlsPanel", new Vector2(1f, 0f), new Vector2(-24f, 96f), new Vector2(780f, 296f));
        Text helpText = MakeText(help, font, 26, FontStyle.Normal, TextAnchor.UpperLeft, new Vector2(18f, 12f), new Vector2(-12f, -12f));
        helpText.text =
            "<b>CONTROLS</b>\n" +
            "W A S D / Arrows  -  walk (on foot) or drive (in a vehicle)\n" +
            "Enter  -  ride / get off a vehicle (stop first)\n" +
            "P  -  pick up customer / drop off customer\n" +
            "Space  -  handbrake (while driving)\n" +
            "Right mouse + drag  -  rotate camera (on foot)\n" +
            "Esc  -  pause menu / back to the Item Shop\n" +
            "M  -  mini map zoom";
        controlsPanel = help.gameObject;
        MakeMenuButton(canvasObj.transform, "CONTROLS", new Vector2(1f, 0f), new Vector2(-24f, 24f), new Vector2(240f, 60f), new Color(0.2f, 0.35f, 0.6f, 0.95f), font, 28, ToggleControls);

        // shown once when the game starts; Time.time stands still behind the level intro, so the wait begins at START
        controlsPanel.SetActive(!controlsSeen);
        if (!controlsSeen) controlsHideAt = Time.time + controlsShowTime;
        controlsSeen = true;

        RectTransform toastPanel = MakePanel(canvasObj.transform, "ToastPanel", new Vector2(0.5f, 0f), new Vector2(0f, 310f), new Vector2(1200f, 72f));
        toastText = MakeText(toastPanel, font, 32, FontStyle.Bold, TextAnchor.MiddleCenter, new Vector2(12f, 4f), new Vector2(-12f, -4f));
        toastBox = toastPanel.gameObject;
        toastBox.SetActive(false);

        BuildMiniMapUi(canvasObj.transform, font);
        BuildResultUi(canvasObj.transform, font);
        BuildPauseUi(canvasObj.transform, font);
        BuildLevelUi(canvasObj.transform, font);
    }

    void ToggleControls()
    {
        controlsHideAt = -1f;
        controlsPanel.SetActive(!controlsPanel.activeSelf);
    }

    static RectTransform MakePanel(Transform parent, string panelName, Vector2 anchor, Vector2 position, Vector2 size)
    {
        GameObject go = new GameObject(panelName, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
        Image img = go.GetComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.65f);
        img.raycastTarget = false;
        return rt;
    }

    static Text MakeText(RectTransform parent, Font font, int size, FontStyle style, TextAnchor align, Vector2 offsetMin, Vector2 offsetMax)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        Text t = go.GetComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = align;
        t.color = Color.white;
        t.supportRichText = true;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    void RefreshUi()
    {
        if (guidanceText == null || driver == null) return;

        Vector3 from, target, forward;
        string place, hint;
        if (current == null)
        {
            float d;
            RideVehicle nearest = NearestVehicle(driver.transform.position, out d);
            from = driver.transform.position;
            target = nearest.Transform.position;
            forward = Quaternion.Euler(0f, camYaw, 0f) * Vector3.forward;
            place = "the " + nearest.displayName.ToLower();
            hint = "Press ENTER next to a vehicle to ride";
        }
        else if (!hasPassenger)
        {
            from = current.Transform.position;
            forward = FlatForward(current.Transform);
            if (customer != null)
            {
                target = customer.transform.position;
                place = "customer at " + customerLabel;
                hint = "Stop close to the customer and press P to pick up";
            }
            else
            {
                target = from;
                place = "next customer";
                hint = "A new customer is on the way...";
            }
        }
        else
        {
            from = current.Transform.position;
            target = destination;
            forward = FlatForward(current.Transform);
            place = "drop-off at " + destinationLabel;
            hint = "Stop at the drop-off and press P   |   " + RideTimeText();
        }

        float dist = FlatDistance(from, target);
        string dir = Direction(from, target, forward);
        string line = (customer == null && current != null && !hasPassenger)
            ? "Waiting..."
            : dir + "  -  " + Mathf.RoundToInt(dist) + " m";

        guidanceText.text =
            "<size=56><b><color=#FFD54A>" + line + "</color></b></size>\n" +
            "<size=30>" + place + "</size>\n" +
            "<size=26>" + hint + "</size>";

        // low-fuel warning (red, flashing) + directions to the nearest gas station
        bool lowFuel = IsLowOnFuel();
        if (fuelWarnBox.activeSelf != lowFuel)
            fuelWarnBox.SetActive(lowFuel);
        if (lowFuel)
        {
            string head = current.fuel.IsEmpty ? "OUT OF FUEL" : "LOW FUEL";
            string message;
            if (stalls.Count == 0)
            {
                message = head + "  -  find a gas station";
            }
            else
            {
                Vector3 vpos = current.Transform.position;
                FuelStall stall = stalls[NearestStall(vpos)];
                float sd = FlatDistance(vpos, stall.position);
                message = sd <= stallRadius
                    ? head + "  -  stay inside the green ring to refuel (" + refuelCost + " coins)"
                    : head + "  -  " + Direction(vpos, stall.position, FlatForward(current.Transform)) + ", " + Mathf.RoundToInt(sd) + " m to " + stall.label;
            }
            float pulse = 0.65f + 0.35f * Mathf.Sin(Time.time * 6f);
            fuelWarnText.text = message;
            fuelWarnText.color = new Color(1f, 0.22f, 0.22f, pulse);
            fuelWarnImage.color = new Color(0.12f, 0f, 0f, 0.8f);
        }
        UpdateStallMarker(lowFuel);
        UpdateRideResultUi();
        RefreshMiniMap();
        RefreshHud();
        RefreshLevelHud();

        if (controlsHideAt >= 0f && Time.time >= controlsHideAt)
        {
            controlsHideAt = -1f;
            controlsPanel.SetActive(false);
        }

        bool showToast = Time.time < toastUntil;
        if (toastBox.activeSelf != showToast)
            toastBox.SetActive(showToast);
        if (showToast)
            toastText.text = toast;
    }
}
