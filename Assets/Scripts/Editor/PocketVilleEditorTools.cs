using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Editor menu items that make PocketVille's features editable in the Inspector.
/// Tools > PocketVille > Set Up Editable Features   adds the radar manager and the navigator to the open scene.
/// GameObject > PocketVille > Speed Radar           adds a radar you can place by hand.
/// </summary>
public static class PocketVilleEditorTools
{
    [MenuItem("Tools/PocketVille/Set Up Editable Features")]
    static void SetUpEditableFeatures()
    {
        RickshawRideSystem ride = Object.FindAnyObjectByType<RickshawRideSystem>();
        CityTraffic traffic = Object.FindAnyObjectByType<CityTraffic>();
        if (ride == null && traffic == null)
        {
            EditorUtility.DisplayDialog("PocketVille", "Open the city scene (PocketVille_MainCity) first.", "OK");
            return;
        }

        string added = "";
        SpeedRadarNetwork network = Object.FindAnyObjectByType<SpeedRadarNetwork>();
        if (network == null)
        {
            GameObject go = new GameObject("Speed Radars");
            Undo.RegisterCreatedObjectUndo(go, "Add Speed Radars");
            network = Undo.AddComponent<SpeedRadarNetwork>(go);
            added += "\n- Speed Radars (radar settings)";
        }

        if (ride != null && ride.GetComponent<RideNavigator>() == null)
        {
            Undo.AddComponent<RideNavigator>(ride.gameObject);
            added += "\n- Ride Navigator on " + ride.gameObject.name + " (navigation settings)";
        }

        EditorSceneManager.MarkSceneDirty(network.gameObject.scene);
        Selection.activeObject = network.gameObject;
        EditorUtility.DisplayDialog("PocketVille",
            (added.Length > 0 ? "Added:" + added : "Everything is already set up.") +
            "\n\nSpeedometer, drop-off and radar-violation settings are on " +
            (ride != null ? ride.gameObject.name : "the RickshawRideSystem object") +
            ". Traffic settings are on the CityTraffic object.\n\nSave the scene (Ctrl+S) to keep this.",
            "OK");
    }

    /// <summary>
    /// For the command line: opens the city scene, adds the editable objects and saves it.
    /// Unity.exe -batchmode -quit -projectPath <path> -executeMethod PocketVilleEditorTools.SetUpCityFromCommandLine
    /// </summary>
    public static void SetUpCityFromCommandLine()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/PocketVille_MainCity.unity", OpenSceneMode.Single);
        if (Object.FindAnyObjectByType<SpeedRadarNetwork>() == null)
        {
            GameObject go = new GameObject("Speed Radars");
            go.AddComponent<SpeedRadarNetwork>();
            Debug.Log("PocketVille setup: added Speed Radars");
        }
        RickshawRideSystem ride = Object.FindAnyObjectByType<RickshawRideSystem>();
        if (ride != null && ride.GetComponent<RideNavigator>() == null)
        {
            ride.gameObject.AddComponent<RideNavigator>();
            Debug.Log("PocketVille setup: added Ride Navigator to " + ride.gameObject.name);
        }
        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        Debug.Log("PocketVille setup: scene saved = " + saved);
    }

    [MenuItem("GameObject/PocketVille/Speed Radar", false, 10)]
    static void CreateSpeedRadar(MenuCommand command)
    {
        GameObject go = new GameObject("Speed Radar (hand placed)");
        BoxCollider zone = go.AddComponent<BoxCollider>();
        zone.isTrigger = true;
        zone.center = new Vector3(0f, 1.5f, 0f);
        zone.size = new Vector3(10f, 4f, 4f);
        go.AddComponent<SpeedRadar>();

        GameObject parent = command.context as GameObject;
        if (parent != null)
            GameObjectUtility.SetParentAndAlign(go, parent);
        else if (SceneView.lastActiveSceneView != null)
            go.transform.position = SceneView.lastActiveSceneView.pivot;

        Undo.RegisterCreatedObjectUndo(go, "Create Speed Radar");
        Selection.activeObject = go;
    }
}
