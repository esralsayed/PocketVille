/// <summary>
/// The level list. Add a row to All to add a level; RickshawRideSystem runs them in order, and
/// which skin a level unlocks is set by unlockLevel in RickshawSkins.All.
/// </summary>
public static class RickshawLevels
{
    public class Level
    {
        public int customers;
        public float timeLimit;        // seconds
        public float minAverageStars;  // 0 = no rating challenge
    }

    // level bonus = average stars x this
    public const int BonusPerStar = 30;

    public static readonly Level[] All =
    {
        Make(2, 180f, 0f),
        Make(3, 210f, 3f),
        Make(3, 180f, 4f),
    };

    static Level Make(int customers, float timeLimit, float minAverageStars)
    {
        Level l = new Level();
        l.customers = customers;
        l.timeLimit = timeLimit;
        l.minAverageStars = minAverageStars;
        return l;
    }
}
