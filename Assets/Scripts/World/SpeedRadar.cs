using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Roadside speed camera. Fines the tuk-tuk the player is driving once per pass when it enters
/// the zone above the posted limit, and reports every violation so "no violations" contracts can
/// fail. Either placed automatically by SpeedRadarNetwork, or by hand from
/// GameObject > PocketVille > Speed Radar: move and turn it in the Scene view so the orange zone
/// covers the road (X = across the road), and set its rules below.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class SpeedRadar : MonoBehaviour
{
    [Header("Rules")]
    [SerializeField] int speedLimitKmh = 25;
    [SerializeField] int fineAmount = 15;
    [Tooltip("Seconds before this radar can fine again, so one pass never fines twice.")]
    [SerializeField] float cooldown = 3f;
    [Tooltip("Readings this close above the limit are forgiven (km/h).")]
    [SerializeField] float tolerance = 1f;
    [Tooltip("Warn the driver when they come this close to the radar, heading towards it (meters).")]
    [SerializeField] float warningDistance = 70f;

    [Header("Model (hand-placed radars)")]
    [Tooltip("Build the pole, camera, signs and lamp when the game starts.")]
    [SerializeField] bool buildModel = true;
    [Tooltip("Paint red-and-white stripes across the road at the zone.")]
    [SerializeField] bool paintRoadStripes = true;
    [Tooltip("Show the SPEED RADAR label above the camera.")]
    [SerializeField] bool showLabel = true;
    [Tooltip("Optional lamp that flashes when a fine is taken. Filled in automatically when the model is built.")]
    [SerializeField] Renderer flashLamp;

    /// <summary>Raised on every radar violation with the coins actually paid (0 if the player had none).</summary>
    public static event System.Action<int> AnyFineCharged;

    static readonly List<SpeedRadar> activeRadars = new List<SpeedRadar>();

    static readonly Color LampIdle = new Color(0.30f, 0.05f, 0.04f, 1f);
    static readonly Color LampFlash = new Color(1f, 0.16f, 0.10f, 1f);

    float nextFineTime;
    float flashUntil;
    bool lampLit;
    bool warned;
    bool builtByCode;
    bool ownsLampMaterial;
    Material lampMaterial;
    RickshawRideSystem rideSystem;

    public int SpeedLimitKmh { get { return speedLimitKmh; } }

    /// <summary>Number of active radars (shown on the mini map).</summary>
    public static int Count { get { return activeRadars.Count; } }

    /// <summary>World position of radar i (0..Count-1).</summary>
    public static Vector3 PositionOf(int i) { return activeRadars[i].transform.position; }

    /// <summary>Sets the radar up when SpeedRadarNetwork creates it from code.</summary>
    public void Configure(int limitKmh, int fine, float cooldownSeconds, float warnDistance, Renderer lamp)
    {
        builtByCode = true;
        speedLimitKmh = Mathf.Max(1, limitKmh);
        fineAmount = Mathf.Max(0, fine);
        cooldown = Mathf.Max(0f, cooldownSeconds);
        warningDistance = Mathf.Max(0f, warnDistance);
        flashLamp = lamp;
        ownsLampMaterial = true;
        PrepareLamp();
    }

    void Reset()
    {
        // sensible zone when the component is added by hand
        BoxCollider zone = GetComponent<BoxCollider>();
        zone.isTrigger = true;
        zone.center = new Vector3(0f, 1.5f, 0f);
        zone.size = new Vector3(10f, 4f, 4f);
    }

    void Awake()
    {
        GetComponent<BoxCollider>().isTrigger = true;
    }

    void Start()
    {
        // a hand-placed radar builds its own model from its zone
        if (!builtByCode && buildModel && flashLamp == null)
        {
            BoxCollider zone = GetComponent<BoxCollider>();
            flashLamp = SpeedRadarNetwork.BuildRadarModel(transform, zone.size.x, zone.size.x * 0.5f + 1.1f,
                speedLimitKmh, paintRoadStripes, showLabel);
            ownsLampMaterial = true;
        }
        PrepareLamp();
    }

    void OnEnable()
    {
        if (!activeRadars.Contains(this))
            activeRadars.Add(this);
    }

    void OnDisable()
    {
        activeRadars.Remove(this);
    }

    void PrepareLamp()
    {
        if (flashLamp == null || lampMaterial != null) return;
        // a model built for this radar has its own material (shared by its front and back lamp);
        // a lamp you assigned yourself gets a copy so the original material is never changed
        lampMaterial = ownsLampMaterial ? flashLamp.sharedMaterial : flashLamp.material;
        SetLamp(false);
    }

    void OnTriggerEnter(Collider other)
    {
        Rigidbody body = other.attachedRigidbody;
        if (body == null) return;

        // Only the tuk-tuk the player is driving: its controller is enabled only while ridden,
        // and ambient NPC traffic has no controller on its physics body.
        TukTukController car = body.GetComponent<TukTukController>();
        if (car == null || !car.isActiveAndEnabled) return;

        // a vehicle has several colliders, so one pass can call this more than once
        if (Time.time < nextFineTime) return;

        float speed = car.SpeedKmh;
        nextFineTime = Time.time + cooldown;
        if (speed <= speedLimitKmh + tolerance)
        {
            Notify("Speed radar: " + Mathf.RoundToInt(speed) + " km/h - OK (limit " + speedLimitKmh + ")");
            return;
        }
        ChargeFine(speed);
    }

    void ChargeFine(float speed)
    {
        int paid = 0;
        MoneyManager money = MoneyManager.Instance;
        if (money != null && fineAmount > 0)
        {
            paid = Mathf.Min(fineAmount, money.CurrentMoney);
            if (paid > 0 && !money.SpendMoney(paid))
                paid = 0;
        }

        if (FindRide() != null)
        {
            if (paid > 0)
                rideSystem.ShowCoinDeduction(paid);
            rideSystem.ShowNotice("SPEED RADAR: " + Mathf.RoundToInt(speed) + " km/h in a " + speedLimitKmh +
                " zone" + (paid > 0 ? "  -" + paid + " coins" : ""));
        }

        flashUntil = Time.time + 0.8f;
        AnyFineCharged?.Invoke(paid);
    }

    RickshawRideSystem FindRide()
    {
        if (rideSystem == null)
            rideSystem = FindAnyObjectByType<RickshawRideSystem>();
        return rideSystem;
    }

    void Notify(string message)
    {
        if (FindRide() != null)
            rideSystem.ShowNotice(message);
    }

    // one heads-up per approach: when the driver gets close while heading to the radar
    void CheckApproach()
    {
        if (warningDistance <= 0f) return;
        Transform car = FindRide() != null ? rideSystem.DrivenVehicle : null;
        if (car == null) return;

        Vector3 delta = transform.position - car.position;
        delta.y = 0f;
        float distance = delta.magnitude;
        if (distance > warningDistance * 1.5f) { warned = false; return; }
        if (warned || distance > warningDistance || distance < 6f) return;

        Vector3 forward = car.forward;
        forward.y = 0f;
        if (Vector3.Dot(forward.normalized, delta / distance) < 0.6f) return;

        warned = true;
        Notify("SPEED RADAR AHEAD in " + Mathf.RoundToInt(distance) + " m - limit " + speedLimitKmh + " km/h");
    }

    void Update()
    {
        CheckApproach();
        if (lampMaterial == null) return;
        bool lit = Time.time < flashUntil && Mathf.Repeat(Time.time * 8f, 1f) < 0.5f;
        if (lit != lampLit) SetLamp(lit);
    }

    void SetLamp(bool lit)
    {
        lampLit = lit;
        if (lampMaterial == null) return;
        Color c = lit ? LampFlash : LampIdle;
        if (lampMaterial.HasProperty("_BaseColor")) lampMaterial.SetColor("_BaseColor", c);
        if (lampMaterial.HasProperty("_Color")) lampMaterial.SetColor("_Color", c);
    }

    // Shows the zone (orange) and where the pole will stand (white) in the Scene view.
    void OnDrawGizmos()
    {
        BoxCollider zone = GetComponent<BoxCollider>();
        if (zone == null) return;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0.55f, 0.1f, 0.25f);
        Gizmos.DrawCube(zone.center, zone.size);
        Gizmos.color = new Color(1f, 0.55f, 0.1f, 1f);
        Gizmos.DrawWireCube(zone.center, zone.size);
        Gizmos.color = Color.white;
        Gizmos.DrawWireSphere(new Vector3(zone.size.x * 0.5f + 1.1f, 1.6f, 0f), 0.3f);
        Gizmos.DrawLine(new Vector3(zone.size.x * 0.5f + 1.1f, 0f, 0f), new Vector3(zone.size.x * 0.5f + 1.1f, 3.2f, 0f));
    }

    /// <summary>
    /// Finds the closest radar in front of a vehicle within range. Used by the HUD speedometer
    /// to warn the driver before they reach the camera.
    /// </summary>
    public static bool TryGetLimitAhead(Vector3 position, Vector3 forward, float range, out int limit)
    {
        limit = 0;
        float best = float.MaxValue;
        for (int i = 0; i < activeRadars.Count; i++)
        {
            SpeedRadar radar = activeRadars[i];
            if (radar == null) continue;
            Vector3 delta = radar.transform.position - position;
            delta.y = 0f;
            float distance = delta.magnitude;
            if (distance > range) continue;
            if (distance > 4f && Vector3.Dot(forward, delta / distance) < 0.5f) continue; // behind or off to the side
            if (distance < best)
            {
                best = distance;
                limit = radar.speedLimitKmh;
            }
        }
        return best < float.MaxValue;
    }
}
