
using System;
using System.Collections.Generic;
using UnityEngine;

public class ProfileManager : MonoBehaviour
{
    public static ProfileManager Instance { get; private set; }

    public const int MaxProfiles = 5;

    private readonly List<PlayerProfileData> profiles =
        new List<PlayerProfileData>();

    public PlayerProfileData ActiveProfile { get; private set; }

    public IReadOnlyList<PlayerProfileData> Profiles =>
        profiles.AsReadOnly();

    public event Action OnProfilesChanged;
    public event Action<PlayerProfileData> OnActiveProfileChanged;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }
    private void Start()
    {
        Debug.Log("PROFILE MANAGER STARTED");
        LoadProfiles();
    }



    // Load all existing profiles from disk.
    private void LoadProfiles()
    {
        profiles.Clear();

        if (SaveManager.Instance == null)
        {
            Debug.LogError("SaveManager not found.");
            return;
        }

        PlayerProfileData[] savedProfiles =
            SaveManager.Instance.LoadAllProfiles();

        foreach (PlayerProfileData profile in savedProfiles)
        {
            if (profiles.Count >= MaxProfiles)
            {
                Debug.LogWarning(
                    "More than 5 save files found. Extra profiles were not loaded.");
                break;
            }

            if (profiles.Exists(p => p.profileId == profile.profileId))
                continue;

            profiles.Add(profile);
        }

        OnProfilesChanged?.Invoke();

        Debug.Log("Loaded " + profiles.Count + " profiles.");
        foreach (PlayerProfileData profile in profiles)
        {
            Debug.Log(
                "LOADED PROFILE: " + profile.profileName +
                " | Money: $" + profile.money +
                " | ID: " + profile.profileId
            );
        }
    }

    // Create a new profile.
    public bool CreateProfile(string profileName)
    {
        if (SaveManager.Instance == null)
            return false;

        if (profiles.Count >= MaxProfiles)
        {
            Debug.LogWarning("Maximum of 5 profiles reached.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(profileName))
        {
            Debug.LogWarning("Profile name cannot be empty.");
            return false;
        }

        profileName = profileName.Trim();

        if (profileName.Length > 20)
        {
            Debug.LogWarning(
                "Profile name cannot exceed 20 characters.");
            return false;
        }

        PlayerProfileData newProfile =
            new PlayerProfileData(profileName);

        // Save to disk before adding to the list.
        if (!SaveManager.Instance.SaveProfile(newProfile))
            return false;

        profiles.Add(newProfile);

        OnProfilesChanged?.Invoke();

        Debug.Log("Profile created: " + profileName);
        return true;
    }

    // Select an existing profile.
    public bool SelectProfile(string profileId)
    {
        PlayerProfileData profile =
            profiles.Find(p => p.profileId == profileId);

        if (profile == null || SaveManager.Instance == null)
        {
            Debug.LogWarning("Profile not found or SaveManager missing.");
            return false;
        }

        string previousLastPlayed = profile.lastPlayedAt;
        profile.lastPlayedAt = DateTime.UtcNow.ToString("o");

        if (!SaveManager.Instance.SaveProfile(profile))
        {
            profile.lastPlayedAt = previousLastPlayed;
            return false;
        }

        ActiveProfile = profile;

        OnActiveProfileChanged?.Invoke(ActiveProfile);

        Debug.Log("Profile selected: " + profile.profileName);
        return true;
    }

    // Delete a profile and its save file.
    public bool DeleteProfile(string profileId)
    {
        PlayerProfileData profile =
            profiles.Find(p => p.profileId == profileId);

        if (profile == null || SaveManager.Instance == null)
            return false;

        if (!SaveManager.Instance.DeleteProfileFile(profileId))
        {
            Debug.LogError("Could not delete profile save.");
            return false;
        }

        if (ActiveProfile == profile)
        {
            ActiveProfile = null;
            OnActiveProfileChanged?.Invoke(null);
        }

        profiles.Remove(profile);
        OnProfilesChanged?.Invoke();

        Debug.Log("Profile deleted: " + profile.profileName);
        return true;
    }

    // Find a profile by its unique ID.
    public PlayerProfileData GetProfile(string profileId)
    {
        return profiles.Find(p => p.profileId == profileId);
    }

    public bool CanCreateProfile()
    {
        return profiles.Count < MaxProfiles;
    }

    // Save the currently selected profile.
    public bool SaveActiveProfile()
    {
        if (ActiveProfile == null || SaveManager.Instance == null)
            return false;

        return SaveManager.Instance.SaveProfile(ActiveProfile);
    }
}
