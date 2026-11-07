using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Roadside speed camera. Fines the tuk-tuk the player is driving once per pass when it enters
/// the zone above the posted limit. Coins are taken the same way red-light fines take them, and
/// every violation is reported so "no violations" contracts can fail.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class SpeedRadar : MonoBehaviour
{
    [SerializeField] int speedLimitKmh = 25;
    [SerializeField] int fineAmount = 15;
    [Tooltip("Seconds before this radar can fine again, so one pass never fines twice.")]
    [SerializeField] float cooldown = 3f;
    [Tooltip("Readings this close above the limit are forgiven (km/h).")]
    [SerializeField] float tolerance = 1f;
    [Tooltip("Optional lamp that flashes when a fine is taken.")]
    [SerializeField] Renderer flashLamp;

    /// <summary>Raised on every radar violation with the coins actually paid (0 if the player had none).</summary>
    public static event System.Action<int> AnyFineCharged;

    static readonly List<SpeedRadar> activeRadars = new List<SpeedRadar>();

    static readonly Color LampIdle = new Color(0.30f, 0.05f, 0.04f, 1f);
    static readonly Color LampFlash = new Color(1f, 0.16f, 0.10f, 1f);

    float nextFineTime;
    float flashUntil;
    bool lampLit;
    Material lampMaterial;
    RickshawRideSystem rideSystem;

    public int SpeedLimitKmh { get { return speedLimitKmh; } }

    /// <summary>Number of active radars (shown on the mini map).</summary>
    public static int Count { get { return activeRadars.Count; } }

    /// <summary>World position of radar i (0..Count-1).</summary>
    public static Vector3 PositionOf(int i) { return activeRadars[i].transform.position; }

    [Tooltip("Warn the driver when they come this close to the radar, heading towards it (meters).")]
    [SerializeField] float warningDistance = 70f;
    bool warned;

    /// <summary>Sets the radar up when it is created from code.</summary>
    public void Configure(int limitKmh, int fine, Renderer lamp)
    {
        speedLimitKmh = Mathf.Max(1, limitKmh);
        fineAmount = Mathf.Max(0, fine);
        flashLamp = lamp;
        PrepareLamp();
    }

    void Awake()
    {
        GetComponent<BoxCollider>().isTrigger = true;
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
        lampMaterial = flashLamp.material; // per-radar copy so lamps flash independently
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
        if (speed <= speedLimitKmh + tolerance)
        {
            nextFineTime = Time.time + cooldown;
            Notify("Speed radar: " + Mathf.RoundToInt(speed) + " km/h - OK (limit " + speedLimitKmh + ")");
            return;
        }

        nextFineTime = Time.time + cooldown;
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

        if (rideSystem == null)
            rideSystem = FindAnyObjectByType<RickshawRideSystem>();
        if (rideSystem != null)
        {
            if (paid > 0)
                rideSystem.ShowCoinDeduction(paid);
            rideSystem.ShowNotice("SPEED RADAR: " + Mathf.RoundToInt(speed) + " km/h in a " + speedLimitKmh +
                " zone" + (paid > 0 ? "  -" + paid + " coins" : ""));
        }

        flashUntil = Time.time + 0.8f;
        AnyFineCharged?.Invoke(paid);
    }

    void Notify(string message)
    {
        if (rideSystem == null)
            rideSystem = FindAnyObjectByType<RickshawRideSystem>();
        if (rideSystem != null)
            rideSystem.ShowNotice(message);
    }

    // one heads-up per approach: when the driver gets close while heading to the radar
    void CheckApproach()
    {
        if (rideSystem == null)
            rideSystem = FindAnyObjectByType<RickshawRideSystem>();
        Transform car = rideSystem != null ? rideSystem.DrivenVehicle : null;
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
