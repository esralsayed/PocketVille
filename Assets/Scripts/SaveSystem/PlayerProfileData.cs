
using System;
using UnityEngine;

[Serializable]
public class PlayerProfileData
{
    // Unique identifier for each profile.
    public string profileId;

    // Name chosen by the player.
    public string profileName;

    // Player economy.
    public int money;

    // Player progress.
    public string currentProfession;

    // Player position.
    public Vector3 playerPosition;

    // Save metadata.
    public string createdAt;
    public string lastPlayedAt;

    // Creates a new profile with default values.
    public PlayerProfileData(string name)
    {
        profileId = Guid.NewGuid().ToString();

        profileName = name;

        money = 500;

        currentProfession = "Unemployed";

        playerPosition = Vector3.zero;

        createdAt = DateTime.UtcNow.ToString("o");
        lastPlayedAt = createdAt;
    }
}
