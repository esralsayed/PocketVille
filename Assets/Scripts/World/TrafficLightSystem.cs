using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Cycles the city traffic lights and fines the driven rickshaw for crossing on red.
/// Each light has an invisible stop line running across the road through the pole.
/// </summary>
public class TrafficLightSystem : MonoBehaviour
{
    public event System.Action<int> FineCharged;
    [Tooltip("Coins lost for driving through a red light.")]
    [SerializeField] int redLightFine = 10;
    [SerializeField] float greenSeconds = 8f;
    [SerializeField] float yellowSeconds = 2f;
    [SerializeField] float redSeconds = 8f;

    enum Lamp { Green, Yellow, Red }

    class Signal
    {
        public Transform transform;
        public Vector3 forward;
        // Line across the road, at right angles to the light's Z axis.
        public Vector3 lineA;
        public Vector3 lineB;
        // Line along the light's Z axis through the pole, for traffic going the other way.
        public Vector3 axisA;
        public Vector3 axisB;
        public int group;
        public int phaseSlot;
        public float timeOffset;
        public Lamp lamp;
        public bool crossed;
        public bool axisCrossed;
        public Renderer renderer;
        public Material[] materials;
        public int[] redSlots;
        public int[] yellowSlots;
        public int[] greenSlots;
    }

    readonly List<Signal> signals = new List<Signal>();
    float[] groupLockedUntil = new float[0];
    GameObject warningBox;
    Text warningText;
    TukTukController[] vehicles;
    RickshawRideSystem rideSystem;
    TukTukController driven;
    Vector3 previousPosition;
    bool hasPrevious;

    void Start()
    {
        StartCoroutine(Setup());
    }

    IEnumerator Setup()
    {
        yield return null;
        vehicles = FindObjectsByType<TukTukController>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
        rideSystem = FindAnyObjectByType<RickshawRideSystem>();
        CollectSignals();
        BuildWarning();
        if (signals.Count == 0)
            Debug.LogWarning("TrafficLightSystem: no traffic lights found in the scene.");
    }

    void OnDestroy()
    {
        for (int i = 0; i < signals.Count; i++)
        {
            Material[] mats = signals[i].materials;
            if (mats == null)
                continue;
            for (int m = 0; m < mats.Length; m++)
            {
                if (mats[m] != null)
                    Destroy(mats[m]);
            }
        }
    }

    void Update()
    {
        float cycle = greenSeconds + yellowSeconds + redSeconds;
        if (cycle < 0.1f)
            cycle = 0.1f;

        for (int i = 0; i < signals.Count; i++)
        {
            Signal signal = signals[i];
            float time = Time.time + signal.timeOffset;
            float t = time - Mathf.Floor(time / cycle) * cycle;
            Lamp lamp = Lamp.Red;
            if (t < greenSeconds)
                lamp = Lamp.Green;
            else if (t < greenSeconds + yellowSeconds)
                lamp = Lamp.Yellow;
            if (lamp != signal.lamp)
            {
                signal.lamp = lamp;
                ApplyLamp(signal);
            }
        }

        TickVehicle();
        RefreshWarning();
    }

    void CollectSignals()
    {
        Transform[] transforms = FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        List<Transform> lights = new List<Transform>();
        for (int i = 0; i < transforms.Length; i++)
        {
            if (!IsTrafficLight(transforms[i]))
                continue;
            lights.Add(transforms[i]);
        }

        List<List<Signal>> groups = new List<List<Signal>>();
        for (int i = 0; i < lights.Count; i++)
        {
            Signal signal = MakeSignal(lights[i]);
            if (signal == null)
                continue;
            List<Signal> group = null;
            int groupIndex = -1;
            for (int g = 0; g < groups.Count; g++)
            {
                if (FlatDistance(groups[g][0].transform.position, signal.transform.position) < 28f)
                {
                    group = groups[g];
                    groupIndex = g;
                    break;
                }
            }
            if (group == null)
            {
                group = new List<Signal>();
                groupIndex = groups.Count;
                groups.Add(group);
            }
            signal.group = groupIndex;
            group.Add(signal);
            signals.Add(signal);
        }

        groupLockedUntil = new float[groups.Count];

        for (int g = 0; g < groups.Count; g++)
        {
            List<Signal> group = groups[g];
            Vector3 axis = group[0].forward;
            float half = (greenSeconds + yellowSeconds + redSeconds) * 0.5f;
            for (int i = 0; i < group.Count; i++)
            {
                float aligned = Mathf.Abs(Vector3.Dot(group[i].forward, axis));
                group[i].phaseSlot = aligned > 0.5f ? 0 : 1;
                group[i].timeOffset = g * 5f + group[i].phaseSlot * half;
                group[i].lamp = Lamp.Green;
                ApplyLamp(group[i]);
            }
        }
    }

    Signal MakeSignal(Transform light)
    {
        Renderer renderer = light.GetComponentInChildren<Renderer>();
        if (renderer == null)
            return null;

        Vector3 forward = light.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;
        forward.Normalize();

        Material[] source = renderer.materials;
        List<int> red = new List<int>();
        List<int> yellow = new List<int>();
        List<int> green = new List<int>();
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == null)
                continue;
            string name = source[i].name.ToLowerInvariant();
            if (name.Contains("red"))
                red.Add(i);
            else if (name.Contains("yellow"))
                yellow.Add(i);
            else if (name.Contains("green"))
                green.Add(i);
            else
                continue;
            source[i].EnableKeyword("_EMISSION");
        }

        Vector3 side = Vector3.Cross(Vector3.up, forward);
        side.y = 0f;
        if (side.sqrMagnitude < 0.001f)
            side = Vector3.right;
        side.Normalize();

        Signal signal = new Signal();
        signal.transform = light;
        signal.forward = forward;
        signal.lineA = light.position - side * 30f;
        signal.lineB = light.position + side * 30f;
        signal.axisA = light.position - forward * 3f;
        signal.axisB = light.position + forward * 18f;
        signal.renderer = renderer;
        signal.materials = source;
        signal.redSlots = red.ToArray();
        signal.yellowSlots = yellow.ToArray();
        signal.greenSlots = green.ToArray();
        return signal;
    }

    static void ApplyLamp(Signal signal)
    {
        Paint(signal, signal.redSlots, signal.lamp == Lamp.Red, new Color(1f, 0.08f, 0.05f), new Color(3.2f, 0.05f, 0.02f));
        Paint(signal, signal.yellowSlots, signal.lamp == Lamp.Yellow, new Color(1f, 0.78f, 0.08f), new Color(2.4f, 1.5f, 0.05f));
        Paint(signal, signal.greenSlots, signal.lamp == Lamp.Green, new Color(0.1f, 0.85f, 0.18f), new Color(0.05f, 2.2f, 0.15f));
    }

    static void Paint(Signal signal, int[] slots, bool on, Color baseColor, Color emission)
    {
        Color dark = new Color(0.015f, 0.015f, 0.015f, 1f);
        for (int i = 0; i < slots.Length; i++)
        {
            int index = slots[i];
            if (index < 0 || index >= signal.materials.Length || signal.materials[index] == null)
                continue;
            Material mat = signal.materials[index];
            mat.SetColor("_BaseColor", on ? baseColor : dark);
            mat.SetColor("_EmissionColor", on ? emission : Color.black);
        }
    }

    void TickVehicle()
    {
        TukTukController current = null;
        if (vehicles == null)
            return;

        for (int i = 0; i < vehicles.Length; i++)
        {
            if (vehicles[i] != null && vehicles[i].enabled)
                current = vehicles[i];
        }

        if (current == null)
        {
            driven = null;
            hasPrevious = false;
            return;
        }

        Vector3 position = current.transform.position;
        if (driven != current || !hasPrevious)
        {
            driven = current;
            previousPosition = position;
            hasPrevious = true;
            return;
        }

        for (int i = 0; i < signals.Count; i++)
            CheckCrossing(signals[i], previousPosition, position);
        previousPosition = position;
    }

    void CheckCrossing(Signal signal, Vector3 from, Vector3 to)
    {
        if (Along(signal, to) > 6f)
            signal.crossed = false;
        if (Mathf.Abs(Side(signal, to)) > 4f)
            signal.axisCrossed = false;

        bool hit = false;
        if (!signal.crossed && CrossesLine(from, to, signal.lineA, signal.lineB))
        {
            signal.crossed = true;
            hit = true;
        }
        if (!signal.axisCrossed && CrossesLine(from, to, signal.axisA, signal.axisB))
        {
            signal.axisCrossed = true;
            hit = true;
        }

        if (!hit || signal.lamp != Lamp.Red)
            return;

        // Driving through a whole junction crosses several lines, so charge once per pass.
        if (signal.group >= 0 && signal.group < groupLockedUntil.Length)
        {
            if (Time.time < groupLockedUntil[signal.group])
                return;
            groupLockedUntil[signal.group] = Time.time + 3f;
        }
        ChargeFine();
    }

    bool IsApproachingRed()
    {
        if (driven == null)
            return false;
        Vector3 position = driven.transform.position;
        for (int i = 0; i < signals.Count; i++)
        {
            Signal signal = signals[i];
            if (signal.lamp != Lamp.Red)
                continue;
            float along = Along(signal, position);
            float side = Mathf.Abs(Side(signal, position));
            if (along > 0.4f && along < 16f && side < 30f)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when a vehicle travelling in the supplied direction is
    /// approaching one of the currently red signals. Used by ambient traffic.
    /// </summary>
    public bool ShouldStopForRed(Vector3 position, Vector3 travelDirection, float range)
    {
        travelDirection.y = 0f;
        if (travelDirection.sqrMagnitude < 0.001f)
            return false;
        travelDirection.Normalize();

        for (int i = 0; i < signals.Count; i++)
        {
            Signal signal = signals[i];
            if (signal.lamp != Lamp.Red)
                continue;

            // A vehicle approaches this pole from its positive-forward side.
            // Direction alignment prevents a nearby perpendicular lane from
            // reacting to the wrong signal at the same junction.
            float alignment = Vector3.Dot(travelDirection, -signal.forward);
            if (alignment < 0.55f)
                continue;

            float along = Along(signal, position);
            float side = Mathf.Abs(Side(signal, position));
            if (along > 1.5f && along < Mathf.Max(2f, range) && side < 7f)
                return true;
        }
        return false;
    }

    void ChargeFine()
    {
        int paid = 0;
        MoneyManager money = MoneyManager.Instance;
        if (money != null && redLightFine > 0)
        {
            paid = Mathf.Min(redLightFine, money.CurrentMoney);
            if (paid > 0)
                money.SpendMoney(paid);
        }

        if (paid <= 0)
            return;

        FineCharged?.Invoke(paid);

        if (rideSystem != null)
        {
            rideSystem.ShowCoinDeduction(paid);
        }
        else
        {
            MoneyHUD hud = FindAnyObjectByType<MoneyHUD>();
            if (hud != null)
                hud.ShowDeduction(paid);
        }
    }

    void RefreshWarning()
    {
        if (warningBox == null)
            return;

        bool show = IsApproachingRed();
        if (warningBox.activeSelf != show)
            warningBox.SetActive(show);
        if (show && warningText != null)
            warningText.text = "Red light  -  stop";
    }

    void BuildWarning()
    {
        Transform parent = null;
        GameObject rideUi = GameObject.Find("RideUI");
        if (rideUi != null)
            parent = rideUi.transform;

        if (parent == null)
        {
            GameObject canvasObj = new GameObject("TrafficLightUI");
            canvasObj.transform.SetParent(transform, false);
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30;
            CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            parent = canvasObj.transform;
        }

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        GameObject panel = new GameObject("TrafficLightWarning", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        RectTransform rect = (RectTransform)panel.transform;
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, 150f);
        rect.sizeDelta = new Vector2(520f, 64f);
        Image image = panel.GetComponent<Image>();
        image.color = new Color(0.62f, 0.08f, 0.08f, 0.94f);
        image.raycastTarget = false;

        GameObject label = new GameObject("Text", typeof(RectTransform), typeof(Text));
        label.transform.SetParent(panel.transform, false);
        RectTransform labelRect = (RectTransform)label.transform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(12f, 4f);
        labelRect.offsetMax = new Vector2(-12f, -4f);
        warningText = label.GetComponent<Text>();
        warningText.font = font;
        warningText.fontSize = 30;
        warningText.fontStyle = FontStyle.Bold;
        warningText.alignment = TextAnchor.MiddleCenter;
        warningText.color = Color.white;
        warningText.raycastTarget = false;
        warningText.text = "Red light  -  stop";

        warningBox = panel;
        warningBox.SetActive(false);
    }

    static bool IsTrafficLight(Transform t)
    {
        string name = t.name;
        if (name.StartsWith("Traffic light"))
            return true;
        if (!name.StartsWith("Traffic_light"))
            return false;
        Transform parent = t.parent;
        return parent == null || !parent.name.StartsWith("Traffic light");
    }

    static bool CrossesLine(Vector3 from, Vector3 to, Vector3 a, Vector3 b)
    {
        float rx = to.x - from.x;
        float rz = to.z - from.z;
        float sx = b.x - a.x;
        float sz = b.z - a.z;
        float denom = rx * sz - rz * sx;
        if (Mathf.Abs(denom) < 0.00001f)
            return false;

        float qx = a.x - from.x;
        float qz = a.z - from.z;
        float t = (qx * sz - qz * sx) / denom;
        float u = (qx * rz - qz * rx) / denom;
        return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
    }

    static float Along(Signal signal, Vector3 world)
    {
        Vector3 delta = world - signal.transform.position;
        delta.y = 0f;
        return Vector3.Dot(delta, signal.forward);
    }

    static float Side(Signal signal, Vector3 world)
    {
        Vector3 delta = world - signal.transform.position;
        delta.y = 0f;
        return Vector3.Dot(delta, Vector3.Cross(Vector3.up, signal.forward));
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
