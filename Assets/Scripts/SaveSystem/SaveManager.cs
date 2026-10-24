
using System;
using System.IO;
using UnityEngine;

public class SaveManager : MonoBehaviour
{
    public static SaveManager Instance { get; private set; }

    private string ProfilesDirectory =>
        Path.Combine(Application.persistentDataPath, "Profiles");

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        Directory.CreateDirectory(ProfilesDirectory);
    }

    public bool SaveProfile(PlayerProfileData profile)
    {
        if (profile == null || !Guid.TryParse(profile.profileId, out Guid id))
        {
            Debug.LogWarning("Cannot save invalid profile.");
            return false;
        }

        try
        {
            string filePath = GetProfilePath(id);
            string json = JsonUtility.ToJson(profile, true);

            // Write to a temporary file before replacing the save.
            string temporaryPath = filePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            if (File.Exists(filePath))
            {
                File.Replace(temporaryPath, filePath, null);
            }
            else
            {
                File.Move(temporaryPath, filePath);
            }

            Debug.Log("Profile saved: " + profile.profileName);
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError("Failed to save profile: " + exception.Message);
            return false;
        }
    }

    public PlayerProfileData LoadProfile(string profileId)
    {
        if (!Guid.TryParse(profileId, out Guid id))
            return null;

        string filePath = GetProfilePath(id);

        if (!File.Exists(filePath))
            return null;

        try
        {
            string json = File.ReadAllText(filePath);
            PlayerProfileData profile =
                JsonUtility.FromJson<PlayerProfileData>(json);

            if (profile == null || profile.profileId != profileId)
                return null;

            return profile;
        }
        catch (Exception exception)
        {
            Debug.LogError("Failed to load profile: " + exception.Message);
            return null;
        }
    }

    public bool DeleteProfileFile(string profileId)
    {
        if (!Guid.TryParse(profileId, out Guid id))
            return false;

        try
        {
            string filePath = GetProfilePath(id);

            if (File.Exists(filePath))
                File.Delete(filePath);

            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError("Failed to delete profile: " + exception.Message);
            return false;
        }
    }

    public PlayerProfileData[] LoadAllProfiles()
    {
        var profiles = new System.Collections.Generic.List<PlayerProfileData>();

        foreach (string filePath in Directory.GetFiles(
                     ProfilesDirectory, "profile_*.json"))
        {
            try
            {
                string json = File.ReadAllText(filePath);
                PlayerProfileData profile =
                    JsonUtility.FromJson<PlayerProfileData>(json);

                if (profile != null &&
                    Guid.TryParse(profile.profileId, out Guid id) &&
                    Path.GetFileName(filePath) ==
                    Path.GetFileName(GetProfilePath(id)))
                {
                    profiles.Add(profile);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "Could not load save: " + exception.Message);
            }
        }

        return profiles.ToArray();
    }

    private string GetProfilePath(Guid id)
    {
        return Path.Combine(
            ProfilesDirectory,
            "profile_" + id.ToString("N") + ".json");
    }
}
