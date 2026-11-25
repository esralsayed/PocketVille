using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Contract UI (ContractPanel prefab): the small level HUD (top right) and the full-screen panel used
/// for choosing a contract and for CONTRACT COMPLETE / FAILED. Its buttons are copies of buttonPrefab.
/// </summary>
public class ContractPanelView : MonoBehaviour
{
    [Header("Level HUD (top right)")]
    public GameObject levelHud;
    public Text levelHudText;

    [Header("Full-screen contract panel")]
    public GameObject panel;
    public Text title;
    public Text body;
    public Button buttonPrefab;
}
