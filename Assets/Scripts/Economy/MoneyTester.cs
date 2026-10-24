
using UnityEngine;
using UnityEngine.InputSystem;

public class MoneyTester : MonoBehaviour
{
    private void Update()
    {
        if (MoneyManager.Instance == null)
            return;

        // Make sure a keyboard is connected.
        if (Keyboard.current == null)
            return;

        // F5: Earn $100.
        if (Keyboard.current.f5Key.wasPressedThisFrame)
        {
            MoneyManager.Instance.AddMoney(100);
        }

        // F6: Spend $50.
        if (Keyboard.current.f6Key.wasPressedThisFrame)
        {
            MoneyManager.Instance.SpendMoney(50);
        }

        // F7: Attempt to spend $1000.
        if (Keyboard.current.f7Key.wasPressedThisFrame)
        {
            MoneyManager.Instance.SpendMoney(1000);
        }

        // F8: Display current balance.
        if (Keyboard.current.f8Key.wasPressedThisFrame)
        {
            Debug.Log(
                "Current Balance: $" +
                MoneyManager.Instance.CurrentMoney
            );
        }
    }
}
