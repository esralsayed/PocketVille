
using System;
using System.Collections.Generic;
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

    // Rickshaw skins bought in the item shop (skin ids) and the one currently equipped.
    public List<string> ownedSkins = new List<string>();
    public string equippedSkin = "";

    // Player progress.
    public string currentProfession;
    public int levelsCompleted;

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

        money = 50; // new profiles start with 50 coins

        currentProfession = "Unemployed";

        playerPosition = Vector3.zero;

        createdAt = DateTime.UtcNow.ToString("o");
        lastPlayedAt = createdAt;
    }
}
