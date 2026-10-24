
using System;
using UnityEngine;

public class MoneyManager : MonoBehaviour
{
    public static MoneyManager Instance { get; private set; }

    [Header("Economy Settings")]
    [SerializeField] private int startingMoney = 500;

    public int CurrentMoney { get; private set; }

    public event Action<int> OnMoneyChanged;

    private void Awake()
    {
        // Prevent duplicate Money Managers.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        // Keep the manager when changing scenes.
        DontDestroyOnLoad(gameObject);

        CurrentMoney = Mathf.Max(0, startingMoney);
    }

    public void AddMoney(int amount)
    {
        if (amount <= 0)
        {
            Debug.LogWarning("Money amount must be positive.");
            return;
        }

        if (amount > int.MaxValue - CurrentMoney)
        {
            Debug.LogWarning("Money balance limit reached.");
            return;
        }

        CurrentMoney += amount;

        OnMoneyChanged?.Invoke(CurrentMoney);

        Debug.Log("Money earned: $" + amount +
                  " | Balance: $" + CurrentMoney);
    }

    public bool SpendMoney(int amount)
    {
        if (amount <= 0)
        {
            Debug.LogWarning("Expense amount must be positive.");
            return false;
        }

        if (CurrentMoney < amount)
        {
            Debug.Log("Not enough money!");
            return false;
        }

        CurrentMoney -= amount;

        OnMoneyChanged?.Invoke(CurrentMoney);

        Debug.Log("Money spent: $" + amount +
                  " | Balance: $" + CurrentMoney);

        return true;
    }

    public bool CanAfford(int amount)
    {
        return amount >= 0 && CurrentMoney >= amount;
    }
}
