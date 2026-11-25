using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Refuelling at gas stations and the green stall marker.
/// </summary>
public partial class RickshawRideSystem
{
    GameObject stallMarker;
    Transform stallArrow;

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
}
