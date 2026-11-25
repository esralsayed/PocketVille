using UnityEngine;
using UnityEngine.UI;

/// <summary>One round blip on the mini map (MiniMapBlip prefab): a coloured dot with a letter.</summary>
public class MiniMapBlip : MonoBehaviour
{
    public Image fill;
    public Text letter;

    public void Setup(Color colour, string text)
    {
        fill.color = colour;
        letter.text = text;
    }
}
