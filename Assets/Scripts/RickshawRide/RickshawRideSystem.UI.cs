using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// On-screen guidance text and the link between this system and the RideCanvas prefab.
/// </summary>
public partial class RickshawRideSystem
{
    void Toast(string message)
    {
        toast = message;
        toastUntil = Time.time + 4f;
    }

    RideNavigator navigator;

    // Guidance along the ROAD route (from RideNavigator), e.g. "Go left in 20 m" or "Go forward  -  80 m".
    // Close to the target (or before the road map is ready) only the distance is shown.
    string RouteGuidance(string routeKey, Vector3 from, Vector3 forward, Vector3 to)
    {
        string direction;
        float meters;
        if (navigator != null && navigator.GetRouteDirections(routeKey, from, forward, to, out direction, out meters))
        {
            int m = Mathf.RoundToInt(meters);
            if (direction == "Go left" || direction == "Go right")
                return m < 3 ? direction + " now" : direction + " in " + m + " m";
            return direction + "  -  " + m + " m";
        }
        int dist = Mathf.RoundToInt(FlatDistance(from, to));
        return dist <= 15 ? "Almost there  -  " + dist + " m" : dist + " m away";
    }

    // ---------- on-screen UI (uGUI, built at runtime under this object) ----------

    // ---------- on-screen UI (RideCanvas prefab in the scene, see Assets/Prefabs/UI/Ride) ----------

    [Header("On-screen UI")]
    [Tooltip("The RideCanvas in the scene. Its panels are prefabs in Assets/Prefabs/UI/Ride.")]
    [SerializeField] RideCanvasView ui;

    // Connects the RideCanvas prefab to this system: grabs its texts/images and hooks up its buttons.
    bool BindUi()
    {
        if (ui == null) ui = GetComponentInChildren<RideCanvasView>(true);
        if (ui == null)
        {
            Debug.LogError("RickshawRideSystem: no RideCanvas assigned (Assets/Prefabs/UI/Ride/RideCanvas.prefab). Disabling.");
            return false;
        }
        ui.gameObject.SetActive(true);
        ui.canvas.worldCamera = rideCamera; // drawn by our own camera so it can't be hidden

        // directions, fuel warning, toasts, controls help
        RideGuidanceView g = ui.guidance;
        guidanceText = g.guidanceText;
        fuelWarnBox = g.fuelWarningBox;
        fuelWarnImage = g.fuelWarningImage;
        fuelWarnText = g.fuelWarningText;
        toastBox = g.toastBox;
        toastText = g.toastText;
        controlsPanel = g.controlsPanel;
        g.controlsButton.onClick.AddListener(ToggleControls);
        fuelWarnBox.SetActive(false);
        toastBox.SetActive(false);

        // shown once when the game starts; Time.time stands still behind the level intro, so the wait begins at START
        controlsPanel.SetActive(!controlsSeen);
        if (!controlsSeen) controlsHideAt = Time.time + controlsShowTime;
        controlsSeen = true;

        // coins, fuel, clock, speed, MENU button
        RideHudView h = ui.hud;
        coinPanel = h.coinPanel;
        coinPanelHome = coinPanel.anchoredPosition;
        coinText = h.coinText;
        coinDeductionText = h.coinDeductionText;
        coinDeductionRt = coinDeductionText.rectTransform;
        coinDeductionText.gameObject.SetActive(false);
        fuelText = h.fuelText;
        fuelFillImage = h.fuelFill;
        fuelFill = h.fuelFill.rectTransform;
        speedPanel = h.speedPanel;
        speedText = h.speedText;
        speedPanel.SetActive(false); // shown only while driving
        worldClockText = h.worldClockText;
        h.menuButton.onClick.AddListener(() => SetPaused(true));

        BindMiniMap(ui.miniMap);

        // ride result
        RideResultView r = ui.rideResult;
        resultPanel = r.gameObject;
        resultTitle = r.title;
        resultStars = r.stars;
        resultMoney = r.money;
        resultNotes = r.notes;
        resultPanel.SetActive(false);

        // pause menu
        PauseMenuView p = ui.pauseMenu;
        pausePanel = p.gameObject;
        p.resumeButton.onClick.AddListener(() => SetPaused(false));
        p.itemShopButton.onClick.AddListener(() => ReturnToMenu(true));
        p.mainMenuButton.onClick.AddListener(() => ReturnToMenu(false));
        p.hint.text = level != null
            ? "Leaving now restarts this level.   Esc = resume"
            : "Your coins and skins are saved automatically.   Esc = resume";
        pausePanel.SetActive(false);

        // contract HUD + contract panel
        ContractPanelView c = ui.contracts;
        levelHud = c.levelHud;
        levelHudText = c.levelHudText;
        levelHud.SetActive(level != null);
        levelPanel = c.panel;
        levelPanelTitle = c.title;
        levelPanelBody = c.body;
        levelButtonPrefab = c.buttonPrefab;
        levelPanel.SetActive(false);
        return true;
    }

    void ToggleControls()
    {
        controlsHideAt = -1f;
        controlsPanel.SetActive(!controlsPanel.activeSelf);
    }

    void RefreshUi()
    {
        if (guidanceText == null || driver == null) return;

        Vector3 from, target, forward;
        string place, hint;
        string routeKey = RideNavigator.RideRouteKey;
        if (current == null)
        {
            routeKey = "walk-to-vehicle";
            float d;
            RideVehicle nearest = NearestVehicle(driver.transform.position, out d);
            from = driver.transform.position;
            target = nearest.Transform.position;
            forward = Quaternion.Euler(0f, camYaw, 0f) * Vector3.forward;
            place = "the " + nearest.displayName.ToLower();
            hint = "Press ENTER next to a vehicle to ride";
        }
        else if (!hasPassenger)
        {
            from = current.Transform.position;
            forward = FlatForward(current.Transform);
            if (customer != null)
            {
                target = customer.transform.position;
                place = "customer at " + customerLabel;
                hint = "Stop close to the customer and press P to pick up";
            }
            else
            {
                target = from;
                place = "next customer";
                hint = "A new customer is on the way...";
            }
        }
        else
        {
            from = current.Transform.position;
            target = destination;
            forward = FlatForward(current.Transform);
            place = "drop-off at " + destinationLabel;
            hint = "Stop at the drop-off and press P   |   " + RideTimeText();
        }

        string line = (customer == null && current != null && !hasPassenger)
            ? "Waiting..."
            : RouteGuidance(routeKey, from, forward, target);

        guidanceText.text =
            "<size=56><b><color=#FFD54A>" + line + "</color></b></size>\n" +
            "<size=30>" + place + "</size>\n" +
            "<size=26>" + hint + "</size>";

        // low-fuel warning (red, flashing) + directions to the nearest gas station
        bool lowFuel = IsLowOnFuel();
        if (fuelWarnBox.activeSelf != lowFuel)
            fuelWarnBox.SetActive(lowFuel);
        if (lowFuel)
        {
            string head = current.fuel.IsEmpty ? "OUT OF FUEL" : "LOW FUEL";
            string message;
            if (stalls.Count == 0)
            {
                message = head + "  -  find a gas station";
            }
            else
            {
                Vector3 vpos = current.Transform.position;
                FuelStall stall = stalls[NearestStall(vpos)];
                float sd = FlatDistance(vpos, stall.position);
                message = sd <= stallRadius
                    ? head + "  -  stay inside the green ring to refuel (" + refuelCost + " coins)"
                    : head + "  -  " + RouteGuidance("gas-station", vpos, FlatForward(current.Transform), stall.position) + " to " + stall.label;
            }
            float pulse = 0.65f + 0.35f * Mathf.Sin(Time.time * 6f);
            fuelWarnText.text = message;
            fuelWarnText.color = new Color(1f, 0.22f, 0.22f, pulse);
            fuelWarnImage.color = new Color(0.12f, 0f, 0f, 0.8f);
        }
        UpdateStallMarker(lowFuel);
        UpdateRideResultUi();
        RefreshMiniMap();
        RefreshHud();
        RefreshLevelHud();

        if (controlsHideAt >= 0f && Time.time >= controlsHideAt)
        {
            controlsHideAt = -1f;
            controlsPanel.SetActive(false);
        }

        bool showToast = Time.time < toastUntil;
        if (toastBox.activeSelf != showToast)
            toastBox.SetActive(showToast);
        if (showToast)
            toastText.text = toast;
    }
}
