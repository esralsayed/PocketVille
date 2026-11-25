using UnityEngine;
using UnityEngine.UI;

/// <summary>Directions box (top), low-fuel warning, toast messages and the controls help (Directions prefab).</summary>
public class RideGuidanceView : MonoBehaviour
{
    [Header("Directions (top centre)")]
    public Text guidanceText;

    [Header("Low fuel warning")]
    public GameObject fuelWarningBox;
    public Image fuelWarningImage;
    public Text fuelWarningText;

    [Header("Toast message (bottom centre)")]
    public GameObject toastBox;
    public Text toastText;

    [Header("Controls help (bottom right)")]
    public GameObject controlsPanel;
    public Button controlsButton;
}
