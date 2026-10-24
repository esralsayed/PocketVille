
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ProfileSelectionUI : MonoBehaviour
{
    [System.Serializable]
    public class ProfileSlot
    {
        public Button button;
        public TMP_Text profileNameText;
        public TMP_Text moneyText;
        public GameObject selectionHighlight;
    }

    [Header("Profile Slots")]
    [SerializeField]
    private ProfileSlot[] slots =
        new ProfileSlot[5];

    private string selectedProfileId;

    private System.Collections.IEnumerator RefreshAfterInitialization()
    {
        yield return null;
        RefreshUI();
    }

    private void OnEnable()
    {
        if (ProfileManager.Instance != null)
        {
            ProfileManager.Instance.OnProfilesChanged += RefreshUI;
            RefreshUI();
        }
    }

    private void OnDisable()
    {
        if (ProfileManager.Instance != null)
        {
            ProfileManager.Instance.OnProfilesChanged -= RefreshUI;
        }
    }

    private void Start()
    {
        // Connect each slot button to its index.
        for (int i = 0; i < slots.Length; i++)
        {
            int index = i;

            if (slots[i].button != null)
            {
                slots[i].button.onClick.AddListener(
                    () => SelectSlot(index));
            }
        }

        StartCoroutine(RefreshAfterInitialization());
    }

    public void RefreshUI()
    {
        if (ProfileManager.Instance == null)
            return;

        var profiles = ProfileManager.Instance.Profiles;

        // Clear selection if the selected profile no longer exists.
        if (!string.IsNullOrEmpty(selectedProfileId) &&
            ProfileManager.Instance.GetProfile(selectedProfileId) == null)
        {
            selectedProfileId = null;
        }

        for (int i = 0; i < slots.Length; i++)
        {
            ProfileSlot slot = slots[i];

            if (i < profiles.Count)
            {
                PlayerProfileData profile = profiles[i];

                if (slot.profileNameText != null)
                    slot.profileNameText.text = profile.profileName;

                if (slot.moneyText != null)
                    slot.moneyText.text = "$" + profile.money.ToString("N0");

                if (slot.button != null)
                    slot.button.interactable = true;

                if (slot.selectionHighlight != null)
                    slot.selectionHighlight.SetActive(
                        selectedProfileId == profile.profileId);
            }
            else
            {
                if (slot.profileNameText != null)
                    slot.profileNameText.text = "Empty Slot";

                if (slot.moneyText != null)
                    slot.moneyText.text = "";

                if (slot.button != null)
                    slot.button.interactable = false;

                if (slot.selectionHighlight != null)
                    slot.selectionHighlight.SetActive(false);
            }
        }
        Debug.Log("ProfileSelectionUI: RefreshUI called!");

        if (ProfileManager.Instance == null)
        {
            Debug.LogError("ProfileManager.Instance is NULL!");
            return;
        }
    }

    private void SelectSlot(int index)
    {
        var profiles = ProfileManager.Instance.Profiles;

        if (index < 0 || index >= profiles.Count)
            return;

        selectedProfileId = profiles[index].profileId;

        RefreshUI();

        Debug.Log("Selected profile card: " +
                  profiles[index].profileName);
    }
}
