
using UnityEngine;
using UnityEngine.InputSystem;

public class ProfileTester : MonoBehaviour
{
    private void Update()
    {
        if (ProfileManager.Instance == null ||
            Keyboard.current == null)
            return;

        // F1: Create a new profile.
        if (Keyboard.current.f1Key.wasPressedThisFrame)
        {
            int number = ProfileManager.Instance.Profiles.Count + 1;

            ProfileManager.Instance.CreateProfile(
                "Player " + number);
        }

        // F2: Display all profiles.
        if (Keyboard.current.f2Key.wasPressedThisFrame)
        {
            Debug.Log("=== SAVED PROFILES ===");

            foreach (PlayerProfileData profile in
                     ProfileManager.Instance.Profiles)
            {
                Debug.Log(
                    profile.profileName +
                    " | Money: $" + profile.money +
                    " | ID: " + profile.profileId);
            }
        }

        // F3: Select the first profile.
        if (Keyboard.current.f3Key.wasPressedThisFrame)
        {
            if (ProfileManager.Instance.Profiles.Count > 0)
            {
                string id =
                    ProfileManager.Instance.Profiles[0].profileId;

                ProfileManager.Instance.SelectProfile(id);
            }
        }

        // F4: Delete the last profile.
        if (Keyboard.current.f4Key.wasPressedThisFrame)
        {
            int count = ProfileManager.Instance.Profiles.Count;

            if (count > 0)
            {
                string id =
                    ProfileManager.Instance.Profiles[count - 1].profileId;

                ProfileManager.Instance.DeleteProfile(id);
            }
        }
    }
}
