using UnityEngine;
using UnityEngine.UI;

/// <summary>Top-left readouts: coins (with the red "-$" pop-up), fuel bar, MENU button, clock and speedometer (RideHUD prefab).</summary>
public class RideHudView : MonoBehaviour
{
    [Header("Coins")]
    public RectTransform coinPanel;
    public Text coinText;
    public Text coinDeductionText;

    [Header("Fuel")]
    public Text fuelText;
    public Image fuelFill;

    [Header("Clock and speed")]
    public Text worldClockText;
    public GameObject speedPanel;
    public Text speedText;

    [Header("Buttons")]
    public Button menuButton;
}
