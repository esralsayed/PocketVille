
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MoneyManager : MonoBehaviour
{
    public static MoneyManager Instance { get; private set; }

    [Header("Economy Settings")]
    [SerializeField] private int startingMoney = 50;

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

        SceneManager.sceneLoaded += OnSceneLoaded;
        LoadFromActiveProfile();
    }

    // Money lives in the active save profile (so the Main Menu item shop and the city share one balance).
    // With no active profile (e.g. playing the city scene directly) it falls back to startingMoney.
    private void LoadFromActiveProfile()
    {
        ProfileManager pm = ProfileManager.Instance;
        if (pm != null && pm.ActiveProfile != null)
            CurrentMoney = Mathf.Max(0, pm.ActiveProfile.money);
        else
            CurrentMoney = Mathf.Max(0, startingMoney);
    }

    private void SaveToActiveProfile()
    {
        ProfileManager pm = ProfileManager.Instance;
        if (pm == null || pm.ActiveProfile == null)
            return;

        pm.ActiveProfile.money = CurrentMoney;
        pm.SaveActiveProfile();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        LoadFromActiveProfile();
        OnMoneyChanged?.Invoke(CurrentMoney);
    }

    private void OnDestroy()
    {
        if (Instance == this)
            SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // Puts the balance back to an earlier amount (e.g. restarting a failed level).
    public void RestoreBalance(int amount)
    {
        CurrentMoney = Mathf.Max(0, amount);

        OnMoneyChanged?.Invoke(CurrentMoney);
        SaveToActiveProfile();
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
        SaveToActiveProfile();

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
        SaveToActiveProfile();

        Debug.Log("Money spent: $" + amount +
                  " | Balance: $" + CurrentMoney);

        return true;
    }

    public bool CanAfford(int amount)
    {
        return amount >= 0 && CurrentMoney >= amount;
    }
}
