using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Star ratings, tips, crash detection and the ride result panel (RideResult prefab).
/// </summary>
public partial class RickshawRideSystem
{
    // ---------- ratings & tips ----------
    [Header("Ratings & tips")]
    [Tooltip("Typical driving speed (m/s) used to work out how long a ride should take.")]
    [SerializeField] float cruiseSpeed = 8f;
    [Tooltip("Time allowed = base + factor x distance / cruise speed.")]
    [SerializeField] float timeBudgetFactor = 1.8f;
    [SerializeField] float timeBudgetBase = 20f;
    [Tooltip("Up to this much over the time limit (0.3 = 30%) costs 1 star; more costs 2.")]
    [SerializeField] float lateTolerance = 0.3f;
    [Tooltip("A speed drop faster than this (m/s^2) counts as a crash. Normal braking is 18, handbrake 36.")]
    [SerializeField] float crashDeceleration = 80f;
    [Tooltip("A crash only counts if the vehicle was going at least this fast (m/s).")]
    [SerializeField] float crashMinSpeed = 3f;
    [SerializeField, Range(0f, 1f)] float tipFiveStars = 0.5f;
    [SerializeField, Range(0f, 1f)] float tipFourStars = 0.25f;

    public struct RideResult
    {
        public int stars;
        public int fare;
        public int tip;
        public int crashes;
        public float seconds;
        public float budget;
    }

    /// <summary>Raised after every completed ride (levels / daily goals hook in here).</summary>
    public event System.Action<RideResult> RideCompleted;
    public int RidesCompleted { get; private set; }
    public float AverageStars { get { return RidesCompleted == 0 ? 0f : (float)totalStars / RidesCompleted; } }

    int totalStars, rideCrashes;
    float rideStartTime, rideTimeBudget, lastCrashTime = -10f, previousSpeed;
    float resultUntil;
    GameObject resultPanel;
    Image[] resultStars;
    Text resultTitle, resultMoney, resultNotes;

    void BeginRide()
    {
        rideStartTime = Time.time;
        rideCrashes = 0;
        previousSpeed = VehicleSpeed(current);
        float distance = FlatDistance(tripStart, destination);
        rideTimeBudget = timeBudgetBase + timeBudgetFactor * distance / Mathf.Max(1f, cruiseSpeed);
    }

    // a crash = the speed of the ridden vehicle collapses much faster than braking can explain
    void DetectCrash()
    {
        if (current == null)
        {
            previousSpeed = 0f;
            return;
        }

        float speed = VehicleSpeed(current);
        float dt = Time.fixedDeltaTime;
        if (hasPassenger && dt > 0f && previousSpeed > crashMinSpeed
            && (previousSpeed - speed) / dt > crashDeceleration
            && Time.time - lastCrashTime > 1f)
        {
            rideCrashes++;
            lastCrashTime = Time.time;
            Toast("Ouch! Your customer felt that.");
        }
        previousSpeed = speed;
    }

    RideResult ScoreRide(int fare)
    {
        float seconds = Time.time - rideStartTime;

        int stars = 5;
        if (seconds > rideTimeBudget * (1f + lateTolerance)) stars -= 2;
        else if (seconds > rideTimeBudget) stars -= 1;
        if (rideCrashes >= 2) stars -= 2;
        else if (rideCrashes == 1) stars -= 1;
        stars = Mathf.Clamp(stars, 1, 5);

        float tipRate = stars == 5 ? tipFiveStars : (stars == 4 ? tipFourStars : 0f);

        RideResult r = new RideResult();
        r.stars = stars;
        r.fare = fare;
        r.tip = Mathf.RoundToInt(fare * tipRate);
        r.crashes = rideCrashes;
        r.seconds = seconds;
        r.budget = rideTimeBudget;

        RidesCompleted++;
        totalStars += stars;
        if (RideCompleted != null) RideCompleted(r);
        return r;
    }

    static string FormatTime(float seconds)
    {
        int t = Mathf.Max(0, Mathf.CeilToInt(seconds));
        return (t / 60) + ":" + (t % 60).ToString("00");
    }

    string RideTimeText()
    {
        float left = rideTimeBudget - (Time.time - rideStartTime);
        if (left >= 0f) return "Time left " + FormatTime(left);
        return "<color=#FF5555>LATE  +" + FormatTime(-left) + "</color>";
    }

    void ShowRideResult(RideResult r)
    {
        if (resultPanel == null) return;

        for (int i = 0; i < resultStars.Length; i++)
            resultStars[i].color = i < r.stars ? new Color(1f, 0.82f, 0.2f, 1f) : new Color(1f, 1f, 1f, 0.18f);

        resultTitle.text = r.stars >= 5 ? "PERFECT RIDE!" : (r.stars == 4 ? "GREAT RIDE" : (r.stars == 3 ? "RIDE COMPLETE" : "ROUGH RIDE"));
        resultMoney.text = "Fare +" + r.fare + (r.tip > 0 ? "      <color=#7CFF8A>Tip +" + r.tip + "</color>" : "      No tip");

        string timeNote = r.seconds <= r.budget
            ? "On time (" + FormatTime(r.seconds) + ")"
            : "Late (" + FormatTime(r.seconds) + " of " + FormatTime(r.budget) + ")";
        string crashNote = r.crashes == 0 ? "No crashes" : r.crashes + (r.crashes == 1 ? " crash" : " crashes");
        resultNotes.text = timeNote + "   -   " + crashNote;

        resultPanel.SetActive(true);
        resultUntil = Time.time + 6f;
    }

    void UpdateRideResultUi()
    {
        if (resultPanel != null && resultPanel.activeSelf && Time.time > resultUntil)
            resultPanel.SetActive(false);
    }
}
