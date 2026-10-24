
using TMPro;
using UnityEngine;

public class MoneyHUD : MonoBehaviour
{
    [SerializeField] private TMP_Text moneyText;

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
    }

    private void UpdateMoney(int amount)
    {
        if (moneyText != null)
        {
            moneyText.text = "$" + amount.ToString("N0");
        }
    }
}
