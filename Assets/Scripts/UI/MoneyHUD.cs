
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MoneyHUD : MonoBehaviour
{
    [SerializeField] private TMP_Text moneyText;

    [Header("Deduction feedback")]
    [SerializeField] private float flashSeconds = 3f;
    [SerializeField] private float flashInterval = 0.25f;
    [SerializeField] private Color deductionTextColor = new Color(1f, 0.15f, 0.12f, 1f);
    [SerializeField] private Color deductionBoxTint = new Color(1f, 0.3f, 0.3f, 1f);

    Image box;
    Color normalTextColor = Color.white;
    Color normalBoxColor = Color.white;
    bool capturedColors;
    float flashEnds;

    private void Awake()
    {
        box = GetComponent<Image>();
    }

    private void OnEnable()
    {
        if (MoneyManager.Instance == null)
        {
            Debug.LogWarning("MoneyManager not found.");
            return;
        }

        MoneyManager.Instance.OnMoneyChanged += UpdateMoney;
        UpdateMoney(MoneyManager.Instance.CurrentMoney);
    }

    private void OnDisable()
    {
        if (MoneyManager.Instance != null)
        {
            MoneyManager.Instance.OnMoneyChanged -= UpdateMoney;
        }
        StopFlash();
    }

    private void Update()
    {
        if (flashEnds <= 0f)
            return;

        float now = Time.unscaledTime;
        if (now >= flashEnds)
        {
            StopFlash();
            return;
        }

        // Blink the box red and normal, with the number held red the whole time.
        int step = Mathf.FloorToInt((flashEnds - now) / Mathf.Max(0.05f, flashInterval));
        bool on = step % 2 == 0;
        if (box != null)
            box.color = on ? deductionBoxTint : normalBoxColor;
        if (moneyText != null)
            moneyText.color = deductionTextColor;
    }

    /// <summary>Flashes the money box red for a few seconds after coins are taken.</summary>
    public void ShowDeduction(int amount)
    {
        if (amount <= 0)
            return;

        CaptureColors();
        flashEnds = Time.unscaledTime + Mathf.Max(0.5f, flashSeconds);
        if (moneyText != null)
            moneyText.color = deductionTextColor;
        if (box != null)
            box.color = deductionBoxTint;
        Refresh();
    }

    void CaptureColors()
    {
        if (capturedColors)
            return;
        if (box == null)
            box = GetComponent<Image>();
        if (box != null)
            normalBoxColor = box.color;
        if (moneyText != null)
            normalTextColor = moneyText.color;
        capturedColors = true;
    }

    void StopFlash()
    {
        flashEnds = 0f;
        if (!capturedColors)
            return;
        if (box != null)
            box.color = normalBoxColor;
        if (moneyText != null)
            moneyText.color = normalTextColor;
    }

    private void UpdateMoney(int amount)
    {
        Refresh();
    }

    void Refresh()
    {
        if (moneyText == null || MoneyManager.Instance == null)
            return;

        moneyText.text = "$" + MoneyManager.Instance.CurrentMoney.ToString("N0");
    }
}
