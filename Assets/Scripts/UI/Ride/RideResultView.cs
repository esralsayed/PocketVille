using UnityEngine;
using UnityEngine.UI;

/// <summary>Panel shown after each drop-off: title, 5 stars, fare / tip and notes (RideResult prefab).</summary>
public class RideResultView : MonoBehaviour
{
    public Text title;
    public Image[] stars = new Image[5];
    public Text money;
    public Text notes;
}
