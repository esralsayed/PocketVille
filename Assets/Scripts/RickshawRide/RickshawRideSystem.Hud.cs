using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Coin, fuel, clock and speed readouts plus the coin-deduction effect (RideHUD prefab).
/// </summary>
public partial class RickshawRideSystem
{
    void RefreshHud()
    {
        if (coinText == null) return;

        int money = MoneyManager.Instance != null ? MoneyManager.Instance.CurrentMoney : 0;
        bool showingDeduction = coinDeductionStarted >= 0f;
        coinText.text = "<color=" + (showingDeduction ? "#FF4D42" : "#FFD54A") + ">$ " + money.ToString("N0") + "</color>";
        UpdateCoinDeductionAnimation();
        if (worldClockText != null)
            worldClockText.text = DayNightSystem.Instance != null ? DayNightSystem.Instance.FormattedTime : "DAY 1";
        UpdateSpeedometer();

        TukTukFuelSystem f = null;
        if (current != null)
        {
            f = current.fuel;
        }
        else
        {
            float d;
            RideVehicle nearest = NearestVehicle(driver.transform.position, out d);
            if (nearest != null) f = nearest.fuel;
        }

        if (f == null)
        {
            fuelText.text = "FUEL  --";
            fuelFill.anchorMax = new Vector2(0f, 1f);
            return;
        }

        float n = f.NormalizedFuel;
        bool low = f.IsLow || f.IsEmpty;
        Color c = low ? new Color(1f, 0.22f, 0.22f, 1f) : new Color(0.96f, 0.77f, 0.24f, 1f);
        fuelFillImage.color = c;
        fuelFill.anchorMax = new Vector2(Mathf.Clamp01(n), 1f);
        fuelText.text = "FUEL  " + Mathf.RoundToInt(n * 100f) + "%" + (f.IsEmpty ? "  EMPTY" : (low ? "  LOW" : ""));
        fuelText.color = low ? c : Color.white;
    }

    void UpdateSpeedometer()
    {
        if (speedPanel == null) return;

        TukTukController car = current != null ? current.driveController as TukTukController : null;
        if (car == null)
        {
            if (speedPanel.activeSelf) speedPanel.SetActive(false);
            shownSpeedKmh = 0f;
            return;
        }
        if (!speedPanel.activeSelf) speedPanel.SetActive(true);

        // smooth the reading so small physics jitter does not make the number flicker
        shownSpeedKmh = Mathf.Lerp(shownSpeedKmh, car.SpeedKmh, 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime));
        int kmh = Mathf.RoundToInt(shownSpeedKmh);

        const float radarWarningRange = 60f;
        int limit;
        bool radarAhead = SpeedRadar.TryGetLimitAhead(car.transform.position, FlatForward(car.transform), radarWarningRange, out limit);
        bool over = radarAhead && kmh > limit;
        string colour = over ? "#FF4D42" : (radarAhead ? "#FFD54A" : "#FFFFFF");
        speedText.text = "<size=24>SPEED</size>  <color=" + colour + ">" + kmh + " km/h</color>" +
            (radarAhead ? "  <size=24><color=#FFD54A>RADAR " + limit + "</color></size>" : "");
    }

    /// <summary>Plays visible feedback on the gameplay coin HUD when coins are taken.</summary>
    public void ShowCoinDeduction(int amount)
    {
        if (amount <= 0 || coinPanel == null || coinDeductionText == null)
            return;

        coinDeductionText.text = "-$" + amount.ToString("N0");
        coinDeductionText.gameObject.SetActive(true);
        coinDeductionStarted = Time.unscaledTime;
        UpdateCoinDeductionAnimation();
    }

    void UpdateCoinDeductionAnimation()
    {
        if (coinDeductionStarted < 0f || coinPanel == null || coinDeductionText == null)
            return;

        float progress = (Time.unscaledTime - coinDeductionStarted) / CoinDeductionDuration;
        if (progress >= 1f)
        {
            coinDeductionStarted = -1f;
            coinPanel.anchoredPosition = coinPanelHome;
            coinPanel.localScale = Vector3.one;
            coinDeductionText.gameObject.SetActive(false);
            return;
        }

        progress = Mathf.Clamp01(progress);
        float fade = 1f - Mathf.SmoothStep(0.65f, 1f, progress);
        float shake = Mathf.Sin(progress * Mathf.PI * 12f) * (1f - progress) * 12f;
        float punch = 1f + Mathf.Sin(progress * Mathf.PI) * 0.18f;

        coinPanel.anchoredPosition = coinPanelHome + new Vector2(shake, 0f);
        coinPanel.localScale = Vector3.one * punch;
        coinDeductionRt.anchoredPosition = new Vector2(0f, -8f - 34f * progress);
        coinDeductionText.color = new Color(1f, 0.18f, 0.12f, fade);
    }
}
