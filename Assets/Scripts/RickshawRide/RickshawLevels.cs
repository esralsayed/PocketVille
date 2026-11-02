using UnityEngine;

/// <summary>
/// The level list. Add a row to All to add a level; RickshawRideSystem runs them in order, and
/// which skin a level unlocks is set by unlockLevel in RickshawSkins.All.
/// </summary>
public static class RickshawLevels
{
    public class Level
    {
        public string title;
        public int customers;
        public float timeLimit;        // seconds
        public float minAverageStars;  // 0 = no rating challenge
        public bool nightShift;
        public bool lowFuelStart;
        public bool noCrashes;
        public bool noViolations;
        public float fareMultiplier = 1f;
        public float trafficSpeedMultiplier = 1f;

        public string Rules()
        {
            string text = "Serve " + customers + " customers in " + FormatTime(timeLimit);
            if (minAverageStars > 0f) text += "\nAverage rating: " + minAverageStars.ToString("0.#") + "+ stars";
            if (noCrashes) text += "\nNo passenger crashes";
            if (noViolations) text += "\nNo red-light violations";
            if (lowFuelStart) text += "\nStart with 35% fuel";
            if (nightShift) text += "\nNight shift";
            if (fareMultiplier > 1f) text += "\n" + fareMultiplier.ToString("0.0") + "x fares";
            return text;
        }
    }

    // level bonus = average stars x this
    public const int BonusPerStar = 30;

    public static readonly Level[] All =
    {
        Make("First Shift", 2, 180f, 0f),
        Make("City Service", 3, 210f, 3f),
        Make("Expert Driver", 3, 180f, 4f),
    };

    static Level Make(string title, int customers, float timeLimit, float minAverageStars)
    {
        Level l = new Level();
        l.title = title;
        l.customers = customers;
        l.timeLimit = timeLimit;
        l.minAverageStars = minAverageStars;
        return l;
    }

    public static Level[] GenerateOffers(int completed)
    {
        int rank = completed + 1;
        int customers = Mathf.Clamp(2 + completed / 2, 2, 6);
        float rating = completed < 2 ? 0f : Mathf.Min(4.5f, 3f + completed * 0.15f);

        Level standard = Make(rank <= All.Length ? All[rank - 1].title : "City Shift " + rank,
            customers, 45f + customers * Mathf.Max(52f, 76f - completed * 2f), rating);
        standard.fareMultiplier = 1f + Mathf.Min(0.5f, completed * 0.04f);
        standard.trafficSpeedMultiplier = 1f + Mathf.Min(0.3f, completed * 0.025f);

        int precisionCustomers = Mathf.Max(2, customers - 1);
        Level precision = Make("Precision Contract", precisionCustomers,
            55f + precisionCustomers * 72f, Mathf.Max(3.5f, rating));
        precision.noCrashes = true;
        precision.noViolations = completed >= 1;
        precision.fareMultiplier = 1.35f + Mathf.Min(0.45f, completed * 0.04f);
        precision.trafficSpeedMultiplier = 1.05f + Mathf.Min(0.35f, completed * 0.03f);

        Level night = Make("Night Rush", customers,
            35f + customers * Mathf.Max(46f, 64f - completed * 1.5f), Mathf.Max(3f, rating));
        night.nightShift = true;
        night.lowFuelStart = completed >= 1;
        night.noViolations = completed >= 3;
        night.fareMultiplier = 1.5f + Mathf.Min(0.5f, completed * 0.05f);
        night.trafficSpeedMultiplier = 1.12f + Mathf.Min(0.38f, completed * 0.03f);

        return new[] { standard, precision, night };
    }

    static string FormatTime(float seconds)
    {
        int value = Mathf.CeilToInt(seconds);
        return (value / 60) + ":" + (value % 60).ToString("00");
    }
}
