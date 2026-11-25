using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Contracts (levels), the contract panel and the pause menu (ContractPanel and PauseMenu prefabs).
/// </summary>
public partial class RickshawRideSystem
{
    // ---------- contracts ----------
    // Every city visit offers three different contracts. Completing one raises the driver's rank and
    // generates a harder set. Failing or leaving restores the coins held when the contract started.
    static int levelsCompletedWithoutProfile; // playing the city scene directly, with no profile

    RickshawLevels.Level level;
    RickshawLevels.Level[] contractOffers;
    int levelNumber, levelStartMoney;
    int contractCrashes, contractViolations;
    bool levelRunning, levelPanelOpen;
    float levelTimeLeft;
    TrafficLightSystem trafficLights;
    GameObject levelHud, levelPanel;
    Text levelHudText, levelPanelTitle, levelPanelBody;
    readonly List<GameObject> levelButtons = new List<GameObject>();

    int LevelsCompleted
    {
        get
        {
            ProfileManager pm = ProfileManager.Instance;
            return pm != null && pm.ActiveProfile != null ? pm.ActiveProfile.levelsCompleted : levelsCompletedWithoutProfile;
        }
    }

    void MarkLevelCompleted()
    {
        ProfileManager pm = ProfileManager.Instance;
        if (pm != null && pm.ActiveProfile != null)
        {
            pm.ActiveProfile.levelsCompleted = levelNumber;
            pm.SaveActiveProfile();
        }
        else
        {
            levelsCompletedWithoutProfile = levelNumber;
        }
    }

    void ChooseLevel()
    {
        int done = LevelsCompleted;
        levelNumber = done + 1;
        contractOffers = RickshawLevels.GenerateOffers(done);
        level = null;
    }

    void ShowLevelIntro()
    {
        OpenLevelPanel("CHOOSE CONTRACT  •  RANK " + levelNumber,
            "Pick a shift. Contracts become harder and more rewarding as your rank grows.\n" +
            "Night Rush starts after dark immediately, so you can play the night city now.");

        for (int i = 0; i < contractOffers.Length; i++)
        {
            int offerIndex = i;
            RickshawLevels.Level offer = contractOffers[i];
            string prefix = i == 0 ? "STANDARD  •  " : (i == 1 ? "PRECISION  •  " : "NIGHT  •  ");
            Color color = i == 0 ? new Color(0.2f, 0.65f, 0.3f, 1f)
                : (i == 1 ? new Color(0.88f, 0.58f, 0.12f, 1f) : new Color(0.25f, 0.32f, 0.7f, 1f));
            AddLevelButton(prefix + offer.title, color, () => SelectContract(offerIndex));
        }
    }

    void SelectContract(int offerIndex)
    {
        level = contractOffers[Mathf.Clamp(offerIndex, 0, contractOffers.Length - 1)];
        levelHud.SetActive(true);
        OpenLevelPanel(level.title.ToUpperInvariant() + "  •  RANK " + levelNumber,
            level.Rules() + "\n\n" + RewardText());
        AddLevelButton("START", new Color(0.2f, 0.65f, 0.3f, 1f), StartLevel);
        AddLevelButton("BACK", new Color(0.3f, 0.33f, 0.4f, 1f), ShowLevelIntro);
    }

    string RewardText()
    {
        RickshawSkins.Skin skin = RickshawSkins.UnlockedBy(levelNumber);
        return "Reward: bonus coins for your average rating" + (skin != null ? " + unlocks " + skin.name : "");
    }

    void StartLevel()
    {
        levelStartMoney = MoneyManager.Instance != null ? MoneyManager.Instance.CurrentMoney : 0;
        levelTimeLeft = level.timeLimit;
        contractCrashes = 0;
        contractViolations = 0;

        if (level.nightShift && DayNightSystem.Instance != null)
            DayNightSystem.Instance.AdvanceToHour(19.5f);

        if (level.lowFuelStart)
            foreach (RideVehicle vehicle in vehicles)
                if (vehicle.fuel != null)
                    vehicle.fuel.SetFuelFraction(0.35f);

        CityTraffic cityTraffic = FindAnyObjectByType<CityTraffic>();
        if (cityTraffic != null)
            cityTraffic.SetDifficulty(level.trafficSpeedMultiplier);

        levelRunning = true;
        levelPanelOpen = false;
        levelPanel.SetActive(false);
        Time.timeScale = 1f;
        Toast(level.nightShift ? "Night shift started — street lights are on." : level.title + " started.");
    }

    void OnTrafficFineCharged(int amount)
    {
        if (levelRunning)
            contractViolations++;
    }

    void TickLevel()
    {
        if (!levelRunning) return;
        levelTimeLeft -= Time.deltaTime;
        if (levelTimeLeft <= 0f)
            FailLevel("Time's up! You served " + RidesCompleted + " of " + level.customers + " customers.");
    }

    void FinishLevel()
    {
        if (level.noCrashes && contractCrashes > 0)
        {
            FailLevel("This contract required clean passenger rides, but you had " + contractCrashes +
                (contractCrashes == 1 ? " crash." : " crashes."));
            return;
        }
        if (level.noViolations && contractViolations > 0)
        {
            FailLevel("This contract allowed no traffic violations (red lights or speed radars). You received " + contractViolations +
                (contractViolations == 1 ? " fine." : " fines."));
            return;
        }

        float average = AverageStars;
        if (average < level.minAverageStars)
        {
            FailLevel("Your average rating was " + average.ToString("0.0") + " stars. You needed " + level.minAverageStars.ToString("0.#") + ".");
            return;
        }

        levelRunning = false;
        int bonus = Mathf.RoundToInt(average * RickshawLevels.BonusPerStar);
        if (MoneyManager.Instance != null)
            MoneyManager.Instance.AddMoney(bonus);
        MarkLevelCompleted();

        string body = "Average rating: " + average.ToString("0.0") + " stars\nLevel bonus: +" + bonus + " coins";
        RickshawSkins.Skin skin = RickshawSkins.UnlockedBy(levelNumber);
        if (skin != null)
            body += "\n\nNew skin in the Item Shop: " + skin.name;
        OpenLevelPanel("CONTRACT COMPLETE!", body);

        AddLevelButton("NEXT CONTRACT", new Color(0.2f, 0.65f, 0.3f, 1f), ReloadCity);
        AddLevelButton("ITEM SHOP", new Color(0.95f, 0.72f, 0.12f, 1f), () => ReturnToMenu(true));
        AddLevelButton("MAIN MENU", new Color(0.3f, 0.33f, 0.4f, 1f), () => ReturnToMenu(false));
    }

    void FailLevel(string reason)
    {
        levelRunning = false;
        RestoreLevelStartMoney();
        OpenLevelPanel("CONTRACT FAILED", reason);
        AddLevelButton("RETRY", new Color(0.2f, 0.65f, 0.3f, 1f), ReloadCity);
        AddLevelButton("MAIN MENU", new Color(0.3f, 0.33f, 0.4f, 1f), () => ReturnToMenu(false));
    }

    // also written to the profile directly: on a scene change MoneyManager may already be gone
    void RestoreLevelStartMoney()
    {
        if (MoneyManager.Instance != null)
            MoneyManager.Instance.RestoreBalance(levelStartMoney);

        ProfileManager pm = ProfileManager.Instance;
        if (pm != null && pm.ActiveProfile != null)
        {
            pm.ActiveProfile.money = levelStartMoney;
            pm.SaveActiveProfile();
        }
    }

    void ReloadCity()
    {
        Time.timeScale = 1f;
        SceneManager.LoadScene(gameObject.scene.name);
    }

    void OpenLevelPanel(string title, string body)
    {
        levelPanelOpen = true;
        Time.timeScale = 0f;
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        if (resultPanel != null) resultPanel.SetActive(false);

        foreach (GameObject b in levelButtons) Destroy(b);
        levelButtons.Clear();
        levelPanelTitle.text = title;
        levelPanelBody.text = body;
        levelPanel.SetActive(true);
    }

    void AddLevelButton(string label, Color color, UnityEngine.Events.UnityAction onClick)
    {
        Button button = Instantiate(levelButtonPrefab, levelPanel.transform);
        button.name = label + " Button";
        ((RectTransform)button.transform).anchoredPosition = new Vector2(0f, -130f - levelButtons.Count * 115f);
        button.image.color = color;
        button.GetComponentInChildren<Text>().text = label;
        button.onClick.AddListener(onClick);
        button.gameObject.SetActive(true);
        levelButtons.Add(button.gameObject);
    }

    Button levelButtonPrefab; // MenuButton prefab, copied for every contract-panel button

    void RefreshLevelHud()
    {
        if (level == null || levelHudText == null) return;

        float time = levelRunning ? levelTimeLeft : level.timeLimit;
        string timeColor = levelRunning && time <= 30f ? "#FF5555" : "#FFD54A";
        string text = level.title.ToUpperInvariant() + "  •  RANK " + levelNumber + "\n" +
            "<size=52><color=" + timeColor + ">" + FormatTime(time) + "</color></size>\n" +
            "Customers " + RidesCompleted + " / " + level.customers;
        if (level.minAverageStars > 0f)
            text += "\n<size=24>Rating " + (RidesCompleted > 0 ? AverageStars.ToString("0.0") : "-") + "  (need " + level.minAverageStars.ToString("0.#") + ")</size>";
        levelHudText.text = text;
    }

    [Header("Pause menu")]
    [SerializeField] string menuSceneName = "MainMenu";

    bool paused;
    GameObject pausePanel;

    // Esc (or the MENU button) pauses the game and offers: Resume / Item Shop / Main Menu.
    // Coins are already saved to the profile on every change, so nothing is lost when leaving.
    void SetPaused(bool value)
    {
        paused = value;
        if (pausePanel != null) pausePanel.SetActive(value);
        Time.timeScale = value ? 0f : 1f;
        AudioListener.pause = value;
        if (value)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
    }

    void ReturnToMenu(bool openShop)
    {
        Time.timeScale = 1f;
        AudioListener.pause = false;
        RickshawSkins.OpenShopOnLoad = openShop;
        SceneManager.LoadScene(menuSceneName);
    }

    void OnDestroy()
    {
        if (trafficLights != null)
            trafficLights.FineCharged -= OnTrafficFineCharged;
        SpeedRadar.AnyFineCharged -= OnTrafficFineCharged;

        // leaving in the middle of a level counts as restarting it
        if (levelRunning) RestoreLevelStartMoney();

        // never leave the game frozen if this scene is unloaded while paused
        Time.timeScale = 1f;
        AudioListener.pause = false;
        if (mapTexture != null) mapTexture.Release();
    }
}
