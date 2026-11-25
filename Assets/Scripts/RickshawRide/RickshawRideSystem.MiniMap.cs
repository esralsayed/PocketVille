using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Round mini map: a top-down snapshot of the city plus blips. The UI comes from the MiniMap prefab.
/// </summary>
public partial class RickshawRideSystem
{
    // ---------- mini map ----------
    // One top-down snapshot of the city is rendered once at start (cheap), then shown in a round window that
    // scrolls and rotates with the camera. Blips: customer, drop-off, gas station, vehicles.
    [Header("Mini map")]
    [Tooltip("Scene objects (by name) whose combined size defines the map area.")]
    [SerializeField] string[] mapRootNames = { "Roads & Infrastructure", "Buildings", "Environment" };
    [SerializeField] float mapMargin = 25f;
    [SerializeField] int mapTextureSize = 2048;
    [Tooltip("Radius of the area shown (meters). M switches between these.")]
    [SerializeField] float[] mapZoomRadii = { 55f, 110f };

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
    MiniMapView miniMapView;
    float mapDiameterUi; // on-screen diameter, read from the MiniMap prefab

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

    // Hooks up the MiniMap prefab: map picture, size, and one blip per customer / drop-off / gas station / vehicle.
    void BindMiniMap(MiniMapView view)
    {
        miniMapView = view;
        if (mapTexture == null)
        {
            view.gameObject.SetActive(false);
            return;
        }

        view.gameObject.SetActive(true);
        mapDiameterUi = ((RectTransform)view.transform).rect.width;
        mapRotator = view.mapRotator;
        mapImageRt = view.mapImage.rectTransform;
        view.mapImage.texture = mapTexture;
        northRt = view.north;

        customerBlip = NewBlip("CustomerBlip", new Color(0.95f, 0.75f, 0.05f, 1f), "$");
        destinationBlip = NewBlip("DestinationBlip", new Color(0.15f, 0.7f, 0.3f, 1f), "D");
        for (int i = 0; i < stalls.Count; i++)
            stallBlips.Add(NewBlip("GasBlip" + i, new Color(0.85f, 0.2f, 0.2f, 1f), "F"));
        for (int i = 0; i < vehicles.Count; i++)
            vehicleBlips.Add(NewBlip("VehicleBlip" + i, new Color(0.2f, 0.5f, 0.9f, 1f), "R"));

        mapReady = true;
    }

    // a copy of the MiniMapBlip prefab under the map's Blips holder (hidden until placed)
    Blip NewBlip(string blipName, Color color, string letter)
    {
        MiniMapBlip b = Instantiate(miniMapView.blipPrefab, miniMapView.blipParent);
        b.name = blipName;
        b.Setup(color, letter);
        Blip blip = new Blip();
        blip.rt = (RectTransform)b.transform;
        blip.fill = b.fill;
        blip.go = b.gameObject;
        blip.go.SetActive(false);
        return blip;
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
        while (radarBlips.Count < SpeedRadar.Count)
        {
            Blip rb = NewBlip("RadarBlip" + radarBlips.Count, new Color(1f, 0.45f, 0.05f, 1f), "S");
            rb.rt.SetSiblingIndex(customerBlip.rt.GetSiblingIndex()); // stay under the player arrow
            rb.rt.localScale = new Vector3(0.85f, 0.85f, 1f);
            radarBlips.Add(rb);
        }
        for (int i = 0; i < radarBlips.Count; i++)
            PlaceBlip(radarBlips[i], i < SpeedRadar.Count, i < SpeedRadar.Count ? SpeedRadar.PositionOf(i) : player, player, scale, cos, sin, edge);

        // little "N" riding on the rim, pointing to north
        northRt.anchoredPosition = new Vector2(-sin, cos) * (mapDiameterUi * 0.5f - 4f);
    }
}
