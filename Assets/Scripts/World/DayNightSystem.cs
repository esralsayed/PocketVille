using System.Collections.Generic;
using UnityEngine;

/// <summary>A pause-aware world clock, sun/sky controller, and street-light manager.</summary>
public class DayNightSystem : MonoBehaviour
{
    public static DayNightSystem Instance { get; private set; }

    [Header("Clock")]
    [SerializeField, Range(0f, 24f)] float startingHour = 8f;
    [SerializeField] float realSecondsPerDay = 1080f;

    [Header("Sun")]
    [SerializeField] Light sun;
    [SerializeField] float daySunIntensity = 1.05f;
    [SerializeField] float nightSunIntensity = 0.04f;
    [SerializeField] Color daySunColor = new Color(1f, 0.96f, 0.84f);
    [SerializeField] Color horizonSunColor = new Color(1f, 0.45f, 0.2f);
    [SerializeField] Color nightAmbientColor = new Color(0.035f, 0.055f, 0.12f);
    [SerializeField] Color dayAmbientColor = new Color(0.72f, 0.78f, 0.86f);

    [Header("Street lights")]
    [SerializeField] int maxActiveStreetLights = 24;
    [SerializeField] float streetLightRange = 15f;
    [SerializeField] float streetLightIntensity = 5f;
    [SerializeField] Color streetLightColor = new Color(1f, 0.72f, 0.34f);

    class Lamp
    {
        public Vector3 position;
        public Light light;
        public readonly List<Renderer> bulbs = new List<Renderer>();
    }

    readonly List<Lamp> lamps = new List<Lamp>();
    readonly List<Lamp> distanceOrder = new List<Lamp>();
    Material runtimeSkybox;
    Material originalSkybox;
    Transform playerVehicle;
    float nextLightRefresh;
    bool bulbsOn;
    static bool hasSessionTime;
    static float sessionHour;
    static int sessionDay = 1;

    public float CurrentHour { get; private set; }
    public int DayNumber { get; private set; } = 1;
    public bool IsNight { get; private set; }
    public string FormattedTime
    {
        get
        {
            int hour24 = Mathf.FloorToInt(CurrentHour) % 24;
            int minute = Mathf.FloorToInt((CurrentHour - Mathf.Floor(CurrentHour)) * 60f);
            int hour12 = hour24 % 12;
            if (hour12 == 0) hour12 = 12;
            return "DAY " + DayNumber + "   " + hour12 + ":" + minute.ToString("00") + (hour24 >= 12 ? " PM" : " AM");
        }
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        CurrentHour = hasSessionTime ? sessionHour : Mathf.Repeat(startingHour, 24f);
        DayNumber = hasSessionTime ? sessionDay : 1;
        hasSessionTime = true;
    }

    void Start()
    {
        if (sun == null) sun = RenderSettings.sun;
        if (sun != null) RenderSettings.sun = sun;

        originalSkybox = RenderSettings.skybox;
        if (originalSkybox != null)
        {
            runtimeSkybox = new Material(originalSkybox);
            runtimeSkybox.name = "Runtime Day Night Skybox";
            RenderSettings.skybox = runtimeSkybox;
        }

        TukTukController vehicle = FindAnyObjectByType<TukTukController>();
        if (vehicle != null) playerVehicle = vehicle.transform;
        BuildStreetLights();
        ApplyWorldLighting();
        RefreshNearbyLights(true);
    }

    void Update()
    {
        float seconds = Mathf.Max(60f, realSecondsPerDay);
        CurrentHour += Time.deltaTime * 24f / seconds;
        if (CurrentHour >= 24f)
        {
            CurrentHour -= 24f;
            DayNumber++;
        }
        sessionHour = CurrentHour;
        sessionDay = DayNumber;

        ApplyWorldLighting();
        if (Time.unscaledTime >= nextLightRefresh)
            RefreshNearbyLights(false);
    }

    public void AdvanceToHour(float hour)
    {
        hour = Mathf.Repeat(hour, 24f);
        if (hour <= CurrentHour + 0.05f) DayNumber++;
        CurrentHour = hour;
        sessionHour = CurrentHour;
        sessionDay = DayNumber;
        ApplyWorldLighting();
        RefreshNearbyLights(true);
    }

    void ApplyWorldLighting()
    {
        float angle = CurrentHour / 24f * 360f - 90f;
        float sunHeight = Mathf.Sin((CurrentHour - 6f) / 24f * Mathf.PI * 2f);
        float daylight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.12f, 0.2f, sunHeight));
        float horizon = 1f - Mathf.Clamp01(Mathf.Abs(sunHeight) * 4f);
        IsNight = daylight < 0.28f;

        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(angle, -32f, 0f);
            sun.intensity = Mathf.Lerp(nightSunIntensity, daySunIntensity, daylight);
            sun.color = Color.Lerp(Color.Lerp(daySunColor, horizonSunColor, horizon), daySunColor, daylight);
            sun.shadows = daylight > 0.08f ? LightShadows.Soft : LightShadows.None;
        }

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = Color.Lerp(nightAmbientColor, dayAmbientColor, daylight);
        RenderSettings.ambientIntensity = Mathf.Lerp(0.35f, 1f, daylight);
        RenderSettings.reflectionIntensity = Mathf.Lerp(0.2f, 1f, daylight);
        RenderSettings.fogColor = Color.Lerp(new Color(0.025f, 0.04f, 0.08f), new Color(0.68f, 0.8f, 0.9f), daylight);

        if (runtimeSkybox != null && runtimeSkybox.HasProperty("_Exposure"))
            runtimeSkybox.SetFloat("_Exposure", Mathf.Lerp(0.16f, 1f, daylight));

        bool shouldGlow = daylight < 0.38f;
        if (shouldGlow != bulbsOn)
        {
            bulbsOn = shouldGlow;
            ApplyBulbGlow(shouldGlow);
            RefreshNearbyLights(true);
        }
    }

    void BuildStreetLights()
    {
        Transform[] all = FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            Transform root = all[i];
            if (!IsStreetLampName(root.name)) continue;
            if (root.parent != null && IsStreetLampName(root.parent.name)) continue;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) continue;
            Bounds bounds = renderers[0].bounds;
            for (int r = 1; r < renderers.Length; r++) bounds.Encapsulate(renderers[r].bounds);

            GameObject lightObject = new GameObject("Night Light");
            lightObject.transform.SetParent(transform, false);
            lightObject.transform.position = new Vector3(bounds.center.x, bounds.max.y - 0.35f, bounds.center.z);
            Light point = lightObject.AddComponent<Light>();
            point.type = LightType.Point;
            point.color = streetLightColor;
            point.intensity = streetLightIntensity;
            point.range = streetLightRange;
            point.shadows = LightShadows.None;
            point.enabled = false;

            Lamp lamp = new Lamp();
            lamp.position = lightObject.transform.position;
            lamp.light = point;
            for (int r = 0; r < renderers.Length; r++)
                if (HasLightMaterial(renderers[r])) lamp.bulbs.Add(renderers[r]);
            lamps.Add(lamp);
        }
    }

    static bool IsStreetLampName(string value)
    {
        string name = value.ToLowerInvariant();
        return name.StartsWith("street_lamp") || name.StartsWith("street lamp");
    }

    static bool HasLightMaterial(Renderer renderer)
    {
        Material[] materials = renderer.sharedMaterials;
        for (int i = 0; i < materials.Length; i++)
            if (materials[i] != null && materials[i].name.ToLowerInvariant().Contains("light")) return true;
        return false;
    }

    void ApplyBulbGlow(bool on)
    {
        MaterialPropertyBlock block = new MaterialPropertyBlock();
        Color emission = on ? streetLightColor * 3.5f : Color.black;
        for (int i = 0; i < lamps.Count; i++)
            for (int r = 0; r < lamps[i].bulbs.Count; r++)
            {
                Renderer renderer = lamps[i].bulbs[r];
                renderer.GetPropertyBlock(block);
                block.SetColor("_EmissionColor", emission);
                renderer.SetPropertyBlock(block);
                block.Clear();
            }
    }

    void RefreshNearbyLights(bool immediate)
    {
        nextLightRefresh = Time.unscaledTime + (immediate ? 0.1f : 0.6f);
        for (int i = 0; i < lamps.Count; i++) lamps[i].light.enabled = false;
        if (!bulbsOn || lamps.Count == 0) return;

        Vector3 focus = playerVehicle != null ? playerVehicle.position : Vector3.zero;
        distanceOrder.Clear();
        distanceOrder.AddRange(lamps);
        distanceOrder.Sort((a, b) => (a.position - focus).sqrMagnitude.CompareTo((b.position - focus).sqrMagnitude));
        int count = Mathf.Min(Mathf.Max(0, maxActiveStreetLights), distanceOrder.Count);
        for (int i = 0; i < count; i++) distanceOrder[i].light.enabled = true;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (runtimeSkybox != null)
        {
            if (RenderSettings.skybox == runtimeSkybox) RenderSettings.skybox = originalSkybox;
            Destroy(runtimeSkybox);
        }
    }
}
