using System;
using UnityEngine;

/// <summary>
/// Fuel tank for a driven vehicle. The tank starts full. Fuel is spent only
/// while the engine is propelling a vehicle that is actually moving.
/// </summary>
public class TukTukFuelSystem : MonoBehaviour
{
    [Header("Tank")]
    [Tooltip("Fuel capacity. The normalized level is current fuel divided by this.")]
    [SerializeField] float maxFuel = 100f;

    [Tooltip("Fuel currently in the tank. Each play session starts full.")]
    [SerializeField] float currentFuel = 100f;

    [Header("Consumption")]
    [Tooltip("Fuel units spent per second at full throttle while moving. With a 100 unit tank, 0.333 lasts about five minutes.")]
    [SerializeField] float consumptionPerSecond = 0.33333334f;

    [Tooltip("Horizontal speed below which the vehicle counts as stationary and uses no fuel, in m/s.")]
    [SerializeField] float movementThreshold = 0.25f;

    [Header("Warning")]
    [Tooltip("Normalized fuel below this value is low. 0.2 is 20 percent.")]
    [SerializeField] [Range(0f, 1f)] float lowFuelThreshold = 0.2f;

    /// <summary>Raised when the normalized fuel level changes. The argument is 0–1.</summary>
    public event Action<float> FuelChanged;

    public float MaxFuel => maxFuel;
    public float CurrentFuel => currentFuel;
    public float ConsumptionPerSecond => consumptionPerSecond;
    public float MovementThreshold => movementThreshold;
    public float LowFuelThreshold => lowFuelThreshold;

    /// <summary>Fuel level from empty (0) to full (1).</summary>
    public float NormalizedFuel => maxFuel <= 0.0001f ? 0f : Mathf.Clamp01(currentFuel / maxFuel);

    public bool IsEmpty => currentFuel <= 0.0001f;
    public bool HasFuel => !IsEmpty;
    public bool IsLow => !IsEmpty && NormalizedFuel < lowFuelThreshold;

    void Awake()
    {
        maxFuel = Mathf.Max(0.01f, maxFuel);
        consumptionPerSecond = Mathf.Max(0f, consumptionPerSecond);
        lowFuelThreshold = Mathf.Clamp01(lowFuelThreshold);
        currentFuel = maxFuel;
    }

    void Start()
    {
        FuelChanged?.Invoke(NormalizedFuel);
    }

    /// <summary>Adds fuel and clamps the result to the tank capacity.</summary>
    public void Refuel(float amount)
    {
        if (amount <= 0f)
            return;

        SetFuel(currentFuel + amount);
    }

    /// <summary>
    /// Spends fuel for engine propulsion. Stationary motion, zero throttle,
    /// and a non-positive timestep spend nothing.
    /// </summary>
    public void ConsumePropulsion(float throttle, float speed, float deltaTime)
    {
        if (deltaTime <= 0f || IsEmpty || consumptionPerSecond <= 0f)
            return;
        if (speed < movementThreshold)
            return;

        float throttleAmount = Mathf.Clamp01(Mathf.Abs(throttle));
        if (throttleAmount <= 0.01f)
            return;

        SetFuel(currentFuel - consumptionPerSecond * throttleAmount * deltaTime);
    }

    void SetFuel(float value)
    {
        float clamped = Mathf.Clamp(value, 0f, maxFuel);
        if (Mathf.Abs(clamped - currentFuel) <= 0.00001f)
            return;

        currentFuel = clamped;
        FuelChanged?.Invoke(NormalizedFuel);
    }
}
