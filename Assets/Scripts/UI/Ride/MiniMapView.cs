using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The round mini map (MiniMap prefab). The city picture is rendered when the level starts and put
/// into mapImage; blips are copies of blipPrefab placed under blipParent. The map's on-screen size is
/// this object's width.
/// </summary>
public class MiniMapView : MonoBehaviour
{
    public RectTransform mapRotator;
    public RawImage mapImage;
    public RectTransform north;
    public Transform blipParent;
    public MiniMapBlip blipPrefab;
}
