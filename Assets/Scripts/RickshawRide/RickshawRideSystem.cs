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
/// The class is split into partial files by topic: RickshawRideSystem.OnFoot / .MiniMap / .Ratings /
/// .Contracts / .Hud / .Fuel / .UI. The on-screen UI lives in the RideCanvas prefab (Assets/Prefabs/UI/Ride).
///
/// ADDING A NEW VEHICLE LATER: either add an entry to the 'Vehicles' list in the Inspector, or call
/// RegisterVehicle(driveController, "Name") from the vehicle's own script. Nothing else changes.
/// The drive controller is any Behaviour that moves the vehicle from input; the fuel system
/// (TukTukFuelSystem) and Rigidbody are found automatically on the controller's object or parents.
///
/// Keys: WASD / arrows = walk or drive, Enter = ride / get off, P = pick up / drop off,
///       Space = handbrake (existing), hold right mouse + drag = rotate camera on foot.
/// </summary>
public partial class RickshawRideSystem : MonoBehaviour
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
        if (!BindUi())
        {
            enabled = false;
            return;
        }
        BuildStallMarker();
        navigator = GetComponent<RideNavigator>();
        if (navigator == null) navigator = gameObject.AddComponent<RideNavigator>();
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
}
