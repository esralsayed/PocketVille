using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Bottom-corner fuel gauge. Listens to a TukTukFuelSystem and never changes driving.
/// </summary>
public class TukTukFuelUI : MonoBehaviour
{
    [SerializeField] TukTukFuelSystem fuel;
    [SerializeField] Image[] segments;
    [SerializeField] TMP_Text percentLabel;
    [SerializeField] TMP_Text statusLabel;

    [Header("Colors")]
    [SerializeField] Color normalColor = new Color(0.961f, 0.769f, 0.235f, 1f);
    [SerializeField] Color lowColor = new Color(0.910f, 0.478f, 0.196f, 1f);
    [SerializeField] Color emptyColor = new Color(0.839f, 0.271f, 0.271f, 1f);
    [SerializeField] Color emptySegmentColor = new Color(1f, 1f, 1f, 0.14f);
    [SerializeField] Color percentColor = new Color(0.965f, 0.945f, 0.894f, 1f);

    void Awake()
    {
        if (fuel == null)
            fuel = FindAnyObjectByType<TukTukFuelSystem>();
    }

    void OnEnable()
    {
        if (fuel == null)
            return;

        fuel.FuelChanged += Refresh;
        Refresh(fuel.NormalizedFuel);
    }

    void Start()
    {
        if (fuel != null)
            Refresh(fuel.NormalizedFuel);
    }

    void OnDisable()
    {
        if (fuel != null)
            fuel.FuelChanged -= Refresh;
    }

    void Refresh(float normalized)
    {
        if (fuel == null)
            return;

        bool empty = fuel.IsEmpty;
        bool low = fuel.IsLow;
        Color active = empty ? emptyColor : (low ? lowColor : normalColor);

        if (segments != null)
        {
            float scaled = Mathf.Clamp01(normalized) * segments.Length;
            for (int i = 0; i < segments.Length; i++)
            {
                Image segment = segments[i];
                if (segment == null)
                    continue;

                float fill = Mathf.Clamp01(scaled - i);
                if (fill <= 0.001f)
                {
                    segment.color = emptySegmentColor;
                    segment.fillAmount = 1f;
                }
                else
                {
                    segment.color = active;
                    segment.fillAmount = fill;
                }
            }
        }

        if (percentLabel != null)
        {
            int percent = Mathf.RoundToInt(Mathf.Clamp01(normalized) * 100f);
            percentLabel.text = percent + "%";
            percentLabel.color = empty || low ? active : percentColor;
        }

        if (statusLabel != null)
        {
            if (empty)
            {
                statusLabel.text = "EMPTY";
                statusLabel.color = emptyColor;
            }
            else if (low)
            {
                statusLabel.text = "LOW FUEL";
                statusLabel.color = lowColor;
            }
            else
            {
                statusLabel.text = "";
            }
        }
    }
}
