using UnityEngine;

/// <summary>
/// Root of the in-game ride UI (RideCanvas prefab). Holds one reference per panel so
/// RickshawRideSystem can find everything. Edit the look in the panel prefabs in Assets/Prefabs/UI/Ride.
/// </summary>
public class RideCanvasView : MonoBehaviour
{
    public Canvas canvas;
    public RideGuidanceView guidance;
    public RideHudView hud;
    public MiniMapView miniMap;
    public RideResultView rideResult;
    public ContractPanelView contracts;
    public PauseMenuView pauseMenu;
}
