using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using TMPro;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Shared skin catalog + recolor helper. Used by the Main Menu item shop AND by the city scene
/// (RickshawRideSystem applies the equipped skin). It recolors only the yellow parts of a vehicle's
/// body texture (the black parts, glass, tyres... stay as they are) and never modifies any asset:
/// it builds a recolored copy of the texture and a copy of the material at runtime.
/// </summary>
public static class RickshawSkins
{
    public class Skin
    {
        public string id;
        public string name;
        public float hue;      // target hue 0..1, or -1 = original colours
        public int price;
        public Color swatch;
        public int unlockLevel; // level that unlocks buying it, 0 = open from the start, ComingSoon = no level yet
    }

    public const int SkinPrice = 100;
    public const int ComingSoon = -1;

    // set by the in-game pause menu so the Main Menu opens the shop immediately
    public static bool OpenShopOnLoad;

    public static readonly Skin[] All =
    {
        Make("classic", "Classic Yellow", -1f,   0,         new Color(0.96f, 0.77f, 0.10f), 0),
        Make("red",     "Ruby Red",       0.00f, SkinPrice, Color.HSVToRGB(0.00f, 0.85f, 0.90f), 0),
        Make("orange",  "Sunset Orange",  0.07f, SkinPrice, Color.HSVToRGB(0.07f, 0.90f, 0.95f), 0),
        Make("green",   "Forest Green",   0.33f, SkinPrice, Color.HSVToRGB(0.33f, 0.80f, 0.75f), 1),
        Make("blue",    "Ocean Blue",     0.60f, SkinPrice, Color.HSVToRGB(0.60f, 0.85f, 0.90f), 2),
        Make("purple",  "Royal Purple",   0.77f, SkinPrice, Color.HSVToRGB(0.77f, 0.70f, 0.85f), 3),
        Make("pink",    "Candy Pink",     0.92f, SkinPrice, Color.HSVToRGB(0.92f, 0.60f, 0.95f), ComingSoon),
    };

    // source-colour range that counts as "body yellow" (hue 22..72 degrees)
    const float SourceHueMin = 0.06f;
    const float SourceHueMax = 0.20f;

    static readonly Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>();
    static readonly Dictionary<string, Material> materialCache = new Dictionary<string, Material>();
    static readonly Dictionary<Material, Material> originalOf = new Dictionary<Material, Material>();

    static Skin Make(string id, string name, float hue, int price, Color swatch, int unlockLevel)
    {
        Skin s = new Skin();
        s.id = id; s.name = name; s.hue = hue; s.price = price; s.swatch = swatch; s.unlockLevel = unlockLevel;
        return s;
    }

    public static bool IsLocked(Skin skin, int levelsCompleted)
    {
        return skin.unlockLevel == ComingSoon || levelsCompleted < skin.unlockLevel;
    }

    /// <summary>The skin a level unlocks, or null if it unlocks none.</summary>
    public static Skin UnlockedBy(int levelNumber)
    {
        foreach (Skin s in All)
            if (s.unlockLevel == levelNumber) return s;
        return null;
    }

    public static Skin Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return All[0];
        foreach (Skin s in All)
            if (s.id == id) return s;
        return All[0];
    }

    /// <summary>Applies a skin to every renderer under root whose material is named materialName.</summary>
    public static void Apply(Transform root, string materialName, Skin skin)
    {
        if (root == null || skin == null) return;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            Material[] mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null) continue;
                Material original;
                if (!originalOf.TryGetValue(m, out original)) original = m;
                if (original.name != materialName) continue;

                Material target = skin.hue < 0f ? original : GetSkinned(original, skin);
                if (target != null && target != m) { mats[i] = target; changed = true; }
            }
            if (changed) r.sharedMaterials = mats;
        }
    }

    static Material GetSkinned(Material original, Skin skin)
    {
        string key = original.GetInstanceID() + ":" + skin.id;
        Material cached;
        if (materialCache.TryGetValue(key, out cached) && cached != null) return cached;

        string prop = TextureProperty(original);
        if (prop == null) return original;

        Texture2D tex = BuildTexture(original.GetTexture(prop), skin);
        Material copy = new Material(original);
        copy.name = original.name;
        copy.SetTexture(prop, tex);
        originalOf[copy] = original;
        materialCache[key] = copy;
        return copy;
    }

    static string TextureProperty(Material m)
    {
        string[] candidates = { "baseColorTexture", "_BaseMap", "_MainTex" };
        foreach (string p in candidates)
            if (m.HasProperty(p) && m.GetTexture(p) != null) return p;
        return null;
    }

    static Texture2D BuildTexture(Texture source, Skin skin)
    {
        string key = source.GetInstanceID() + ":" + skin.id;
        Texture2D cached;
        if (textureCache.TryGetValue(key, out cached) && cached != null) return cached;

        // read the source pixels through a RenderTexture (works even if the texture is not readable)
        RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, rt);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(source.width, source.height, TextureFormat.RGBA32, true, false);
        tex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);

        Color32[] px = tex.GetPixels32();
        for (int i = 0; i < px.Length; i++)
        {
            Color32 p = px[i];
            if (p.a == 0) continue;
            float r = p.r / 255f, g = p.g / 255f, b = p.b / 255f;
            float max = Mathf.Max(r, Mathf.Max(g, b));
            float min = Mathf.Min(r, Mathf.Min(g, b));
            float d = max - min;
            if (max < 0.25f || d / max < 0.3f) continue; // too dark or too grey: leave alone

            float h;
            if (max == r) h = ((g - b) / d) % 6f;
            else if (max == g) h = (b - r) / d + 2f;
            else h = (r - g) / d + 4f;
            h /= 6f;
            if (h < 0f) h += 1f;
            if (h < SourceHueMin || h > SourceHueMax) continue; // not body-yellow

            Color c = Color.HSVToRGB(skin.hue, d / max, max);
            px[i] = new Color32((byte)(c.r * 255f + 0.5f), (byte)(c.g * 255f + 0.5f), (byte)(c.b * 255f + 0.5f), p.a);
        }
        tex.SetPixels32(px);
        tex.wrapMode = source.wrapMode;
        tex.filterMode = source.filterMode;
        tex.Apply(true, false);
        tex.name = source.name + "_" + skin.id;

        textureCache[key] = tex;
        return tex;
    }
}

/// <summary>
/// Main Menu "Item Shop" (Fortnite-style, outside the gameplay loop). Lives on ONE new root object in the
/// Main Menu scene; builds its own canvas at runtime, so no existing menu object is edited.
/// Skins cost 100 coins each; owned skins and the equipped skin are saved in the active profile.
/// </summary>
public class RickshawSkinShop : MonoBehaviour
{
    [Tooltip("The rickshaw model used for the rotating preview (the .glb in Assets/Arts/Models/Toktok).")]
    [SerializeField] GameObject rickshawModelPrefab;
    [SerializeField] Vector3 modelEuler = new Vector3(270f, 270f, 0f);
    [SerializeField] string bodyMaterialName = "body";
    [SerializeField] float spinSpeed = 28f;

    class Card
    {
        public RickshawSkins.Skin skin;
        public Image frame;
        public Text status;
        public Button button;
    }

    [System.Serializable]
    public class CardView
    {
        public Image frame;
        public Text status;
        public Button button;
    }

    // ---- UI references. When these are empty the shop builds itself in code at runtime (fallback).
    // "Bake Shop Into Scene" fills them in and creates the real objects, which you can then edit freely. ----
    [Header("Shop UI (filled by 'Bake Shop Into Scene')")]
    [SerializeField] Canvas shopCanvas;
    [SerializeField] GameObject shopRoot, openButton;
    [SerializeField] Text balanceText, messageText, actionText, selectedName;
    [SerializeField] Button actionButton, closeButton;
    [SerializeField] Image actionImage;
    [SerializeField] CardView[] cardViews;

    [Header("3D preview (filled by 'Bake Shop Into Scene')")]
    [SerializeField] RenderTexture previewRT;
    [SerializeField] Camera previewCam;
    [SerializeField] GameObject previewModel;

    Font font;
    readonly List<Card> cards = new List<Card>();
    Vector3 modelCenter;
    int selected;
    bool open;
    bool baking, usingBaked;

    // ---------- profile menu ----------
    // The CREATE PROFILE / PLAY buttons in the profile screen are painted into the panel artwork and have no
    // Button component, so nothing happened on click. This adds invisible click areas over them (and over the
    // five profile cards) at runtime only; no existing menu object is modified in the saved scene.
    [Header("Profile menu")]
    [SerializeField] string gameplaySceneName = "PocketVille_MainCity";

    Text activeProfileText, profileMessageText;
    GameObject profileMessageBox, createPopup;
    TMP_InputField nameInput;
    float profileMessageUntil;

    void BuildProfileMenu(Transform canvas)
    {
        RectTransform areas = new GameObject("ProfileHitAreas", typeof(RectTransform)).GetComponent<RectTransform>();
        areas.SetParent(canvas, false);
        areas.anchorMin = Vector2.zero;
        areas.anchorMax = Vector2.one;
        areas.offsetMin = Vector2.zero;
        areas.offsetMax = Vector2.zero;

        // positions measured from the artwork, as fractions of the screen
        MakeHitArea(areas, "CreateProfileHit", 0.061f, 0.268f, OnCreateProfileClicked);
        MakeHitArea(areas, "PlayHit", 0.503f, 0.750f, OnPlayClicked);

        // clicking a profile card makes it the active profile
        for (int i = 0; i < 5; i++)
        {
            GameObject slot = GameObject.Find("ProfileSlot" + (i + 1));
            if (slot == null) continue;
            Button b = slot.GetComponent<Button>();
            int index = i;
            if (b != null) b.onClick.AddListener(() => OnSlotClicked(index));
        }

        // CREATE / CANCEL buttons inside the existing (hidden) popup, added at runtime only
        ProfileSelectionUI ui = FindFirstObjectByType<ProfileSelectionUI>(FindObjectsInactive.Include);
        if (ui != null)
        {
            Transform popup = ui.transform.Find("CreateProfilePopup");
            if (popup != null)
            {
                createPopup = popup.gameObject;
                Transform input = popup.Find("ProfileNameInput");
                if (input != null) nameInput = input.GetComponent<TMP_InputField>();
                MakePopupButton(popup, "ConfirmCreateButton", "CREATE", new Vector2(-150f, 30f), new Color(0.2f, 0.65f, 0.3f, 1f), ConfirmCreate);
                MakePopupButton(popup, "CancelCreateButton", "CANCEL", new Vector2(150f, 30f), new Color(0.55f, 0.25f, 0.25f, 1f), CancelCreate);
            }
        }

        // which profile is active + short messages
        RectTransform activeRt = MakeRect(canvas, "ActiveProfile", new Vector2(0f, 1f), new Vector2(310f, -30f), new Vector2(560f, 56f));
        Image activeBg = activeRt.gameObject.AddComponent<Image>();
        activeBg.color = new Color(0f, 0f, 0f, 0.6f);
        activeBg.raycastTarget = false;
        activeProfileText = MakeText(activeRt, "", 30, FontStyle.Bold, TextAnchor.MiddleLeft, Color.white);

        RectTransform msgRt = MakeRect(canvas, "ProfileMessage", new Vector2(0.5f, 0.5f), new Vector2(0f, -420f), new Vector2(1000f, 64f));
        Image msgBg = msgRt.gameObject.AddComponent<Image>();
        msgBg.color = new Color(0f, 0f, 0f, 0.85f);
        msgBg.raycastTarget = false;
        profileMessageText = MakeText(msgRt, "", 32, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(1f, 0.9f, 0.5f, 1f));
        profileMessageBox = msgRt.gameObject;
        profileMessageBox.SetActive(false);
    }

    static void MakeHitArea(RectTransform parent, string areaName, float x0, float x1, UnityAction onClick)
    {
        GameObject go = new GameObject(areaName, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(x0, 0.097f);
        rt.anchorMax = new Vector2(x1, 0.190f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        Image img = go.GetComponent<Image>();
        img.color = Color.white;
        Button btn = go.GetComponent<Button>();
        btn.targetGraphic = img;
        ColorBlock cb = btn.colors;
        cb.normalColor = new Color(1f, 1f, 1f, 0f);      // invisible...
        cb.highlightedColor = new Color(1f, 1f, 1f, 0.18f); // ...lights up slightly on hover
        cb.pressedColor = new Color(1f, 1f, 1f, 0.32f);
        cb.selectedColor = new Color(1f, 1f, 1f, 0f);
        cb.disabledColor = new Color(1f, 1f, 1f, 0f);
        cb.colorMultiplier = 1f;
        cb.fadeDuration = 0.08f;
        btn.colors = cb;
        btn.onClick.AddListener(onClick);
    }

    static void MakePopupButton(Transform parent, string buttonName, string label, Vector2 pos, Color color, UnityAction onClick)
    {
        GameObject go = new GameObject(buttonName, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0.5f, 0f);
        rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(250f, 70f);

        Image img = go.GetComponent<Image>();
        img.color = color;
        Button btn = go.GetComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);

        GameObject lab = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        lab.transform.SetParent(go.transform, false);
        RectTransform lr = (RectTransform)lab.transform;
        lr.anchorMin = Vector2.zero;
        lr.anchorMax = Vector2.one;
        lr.offsetMin = Vector2.zero;
        lr.offsetMax = Vector2.zero;
        TextMeshProUGUI t = lab.GetComponent<TextMeshProUGUI>();
        t.text = label;
        t.fontSize = 34;
        t.fontStyle = FontStyles.Bold;
        t.alignment = TextAlignmentOptions.Center;
        t.color = Color.white;
        t.raycastTarget = false;
    }

    void ProfileMessage(string message)
    {
        if (profileMessageText == null) return;
        profileMessageText.text = message;
        profileMessageUntil = Time.unscaledTime + 3.5f;
    }

    void RefreshProfileUi()
    {
        if (activeProfileText != null)
        {
            PlayerProfileData p = Profile;
            activeProfileText.text = p != null ? "Playing as: " + p.profileName : "No profile selected";
        }
        if (profileMessageBox != null)
        {
            bool show = Time.unscaledTime < profileMessageUntil && !open;
            if (profileMessageBox.activeSelf != show) profileMessageBox.SetActive(show);
        }
    }

    void OnCreateProfileClicked()
    {
        if (ProfileManager.Instance == null || createPopup == null)
        {
            ProfileMessage("Profile system not found.");
            return;
        }
        if (ProfileManager.Instance.Profiles.Count >= ProfileManager.MaxProfiles)
        {
            ProfileMessage("All " + ProfileManager.MaxProfiles + " profile slots are used.");
            return;
        }
        createPopup.SetActive(true);
        if (nameInput != null)
        {
            nameInput.text = "";
            nameInput.Select();
            nameInput.ActivateInputField();
        }
    }

    void CancelCreate()
    {
        if (createPopup != null) createPopup.SetActive(false);
    }

    void ConfirmCreate()
    {
        if (ProfileManager.Instance == null || nameInput == null) return;

        string profileName = nameInput.text.Replace("\u200B", "").Trim();
        if (profileName.Length == 0) { ProfileMessage("Type a profile name first."); return; }
        if (profileName.Length > 20) { ProfileMessage("Names can be at most 20 characters."); return; }

        if (!ProfileManager.Instance.CreateProfile(profileName))
        {
            ProfileMessage("Could not create the profile.");
            return;
        }

        createPopup.SetActive(false);
        IReadOnlyList<PlayerProfileData> list = ProfileManager.Instance.Profiles;
        ProfileManager.Instance.SelectProfile(list[list.Count - 1].profileId);
        ProfileMessage("Profile created: " + profileName);
    }

    void OnSlotClicked(int index)
    {
        if (ProfileManager.Instance == null) return;
        IReadOnlyList<PlayerProfileData> list = ProfileManager.Instance.Profiles;
        if (index < 0 || index >= list.Count) return;
        if (ProfileManager.Instance.SelectProfile(list[index].profileId))
            ProfileMessage("Selected: " + list[index].profileName);
    }

    void OnPlayClicked()
    {
        if (Profile == null)
        {
            ProfileMessage("Select or create a profile first.");
            return;
        }
        SceneManager.LoadScene(gameplaySceneName);
    }


    PlayerProfileData Profile
    {
        get { return ProfileManager.Instance != null ? ProfileManager.Instance.ActiveProfile : null; }
    }

    void Start()
    {
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // If the shop was baked into the scene it already exists as real objects; otherwise build it now.
        usingBaked = IsBaked();
        if (usingBaked)
        {
            SetupBaked();
        }
        else
        {
            BuildPreview();
            BuildUi();
        }
        shopRoot.SetActive(false);

        // coming back from the in-game pause menu with "ITEM SHOP": open the shop right away
        if (RickshawSkins.OpenShopOnLoad)
        {
            RickshawSkins.OpenShopOnLoad = false;
            OpenShop();
        }
    }

    bool IsBaked()
    {
        return shopCanvas != null && shopRoot != null && openButton != null && actionButton != null
            && closeButton != null && previewCam != null && previewRT != null
            && cardViews != null && cardViews.Length == RickshawSkins.All.Length;
    }

    // The shop already exists in the scene: only hook up the code-side lists.
    void SetupBaked()
    {
        cards.Clear();
        for (int i = 0; i < cardViews.Length; i++)
        {
            Card c = new Card();
            c.skin = RickshawSkins.All[i];
            c.frame = cardViews[i].frame;
            c.status = cardViews[i].status;
            c.button = cardViews[i].button;
            cards.Add(c);
        }

        if (previewModel != null)
        {
            Bounds b = new Bounds(previewModel.transform.position, Vector3.zero);
            bool any = false;
            foreach (Renderer r in previewModel.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            modelCenter = b.center;
        }
        previewCam.enabled = false;

        // the profile-screen click areas are still created at runtime; keep them underneath the shop screen
        BuildProfileMenu(shopCanvas.transform);
        foreach (string objName in new[] { "ProfileHitAreas", "ActiveProfile", "ProfileMessage" })
        {
            Transform t = shopCanvas.transform.Find(objName);
            if (t != null) t.SetAsFirstSibling();
        }
    }

    void OnDestroy()
    {
        if (!usingBaked && previewRT != null) previewRT.Release();
    }

    void Update()
    {
        RefreshProfileUi();
        if (!open) return;

        if (previewModel != null)
            previewModel.transform.RotateAround(modelCenter, Vector3.up, spinSpeed * Time.deltaTime);

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            CloseShop();
    }

    // ---------- 3D preview ----------

    void BuildPreview()
    {
        GameObject root = new GameObject("SkinShopPreview");
        root.transform.SetParent(transform, false);
        root.transform.position = new Vector3(5000f, 5000f, 5000f);

        if (previewRT == null) previewRT = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGB32);

        GameObject camObj = new GameObject("PreviewCamera");
        camObj.transform.SetParent(root.transform, false);
        previewCam = camObj.AddComponent<Camera>();
        previewCam.clearFlags = CameraClearFlags.SolidColor;
        previewCam.backgroundColor = new Color(0.10f, 0.12f, 0.18f, 1f);
        previewCam.fieldOfView = 30f;
        previewCam.nearClipPlane = 0.1f;
        previewCam.farClipPlane = 200f;
        previewCam.targetTexture = previewRT;
        previewCam.enabled = false;

        if (rickshawModelPrefab == null)
        {
            Debug.LogWarning("RickshawSkinShop: no rickshaw model assigned; the preview will be empty.");
            return;
        }

        previewModel = Instantiate(rickshawModelPrefab, root.transform);
        previewModel.transform.localPosition = Vector3.zero;
        previewModel.transform.rotation = Quaternion.Euler(modelEuler);

        Bounds b = new Bounds(previewModel.transform.position, Vector3.zero);
        bool any = false;
        foreach (Renderer r in previewModel.GetComponentsInChildren<Renderer>())
        {
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        modelCenter = b.center;

        float radius = Mathf.Max(0.5f, b.extents.magnitude);
        float dist = radius / Mathf.Sin(previewCam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
        float pitch = 18f * Mathf.Deg2Rad;
        previewCam.transform.position = modelCenter + new Vector3(0f, Mathf.Sin(pitch) * dist, -Mathf.Cos(pitch) * dist);
        previewCam.transform.LookAt(modelCenter);
    }

    // ---------- UI ----------

    void BuildUi()
    {
        GameObject canvasObj = new GameObject("ItemShopCanvas");
        canvasObj.transform.SetParent(transform, false);
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasObj.AddComponent<GraphicRaycaster>();
        shopCanvas = canvas;

        // floating button that opens the shop
        RectTransform openRt = MakeRect(canvasObj.transform, "OpenShopButton", new Vector2(1f, 1f), new Vector2(-30f, -30f), new Vector2(300f, 84f));
        Image openImg = openRt.gameObject.AddComponent<Image>();
        openImg.color = new Color(0.95f, 0.72f, 0.12f, 1f);
        Button openBtn = openRt.gameObject.AddComponent<Button>();
        openBtn.targetGraphic = openImg;
        openBtn.onClick.AddListener(OpenShop);
        MakeText(openRt, "ITEM SHOP", 36, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(0.1f, 0.1f, 0.1f, 1f));
        openButton = openRt.gameObject;

        if (!baking) BuildProfileMenu(canvasObj.transform);

        // shop screen
        RectTransform rootRt = MakeRect(canvasObj.transform, "ShopRoot", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4000f, 3000f));
        Image dim = rootRt.gameObject.AddComponent<Image>();
        dim.color = new Color(0.04f, 0.05f, 0.08f, 0.97f);
        shopRoot = rootRt.gameObject;

        RectTransform title = MakeRect(rootRt, "Title", new Vector2(0.5f, 0.5f), new Vector2(-760f, 440f), new Vector2(900f, 90f));
        MakeText(title, "ITEM SHOP  -  Rickshaw Skins", 56, FontStyle.Bold, TextAnchor.MiddleLeft, Color.white);

        RectTransform balRt = MakeRect(rootRt, "Balance", new Vector2(0.5f, 0.5f), new Vector2(660f, 440f), new Vector2(520f, 90f));
        balanceText = MakeText(balRt, "", 52, FontStyle.Bold, TextAnchor.MiddleRight, new Color(1f, 0.84f, 0.3f, 1f));

        // preview
        RectTransform previewRt = MakeRect(rootRt, "Preview", new Vector2(0.5f, 0.5f), new Vector2(-560f, 20f), new Vector2(760f, 760f));
        RawImage raw = previewRt.gameObject.AddComponent<RawImage>();
        raw.texture = previewRT;
        raw.raycastTarget = false;

        RectTransform nameRt = MakeRect(rootRt, "SelectedName", new Vector2(0.5f, 0.5f), new Vector2(-560f, -400f), new Vector2(760f, 70f));
        selectedName = MakeText(nameRt, "", 44, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);

        // cards: 4 columns x 2 rows
        for (int i = 0; i < RickshawSkins.All.Length; i++)
        {
            int col = i % 4, row = i / 4;
            Vector2 pos = new Vector2(25f + col * 250f, 150f - row * 290f);
            BuildCard(rootRt, i, pos);
        }

        // action + close
        RectTransform actRt = MakeRect(rootRt, "ActionButton", new Vector2(0.5f, 0.5f), new Vector2(500f, -360f), new Vector2(520f, 100f));
        actionImage = actRt.gameObject.AddComponent<Image>();
        actionButton = actRt.gameObject.AddComponent<Button>();
        actionButton.targetGraphic = actionImage;
        actionButton.onClick.AddListener(OnAction);
        actionText = MakeText(actRt, "", 42, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);

        RectTransform closeRt = MakeRect(rootRt, "CloseButton", new Vector2(0.5f, 0.5f), new Vector2(-760f, -480f), new Vector2(240f, 70f));
        Image closeImg = closeRt.gameObject.AddComponent<Image>();
        closeImg.color = new Color(0.3f, 0.33f, 0.4f, 1f);
        Button closeBtn = closeRt.gameObject.AddComponent<Button>();
        closeBtn.targetGraphic = closeImg;
        closeBtn.onClick.AddListener(CloseShop);
        closeButton = closeBtn;
        MakeText(closeRt, "BACK  (Esc)", 32, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);

        RectTransform msgRt = MakeRect(rootRt, "Message", new Vector2(0.5f, 0.5f), new Vector2(500f, -455f), new Vector2(760f, 60f));
        messageText = MakeText(msgRt, "", 30, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(1f, 0.45f, 0.4f, 1f));
    }

    void BuildCard(RectTransform parent, int index, Vector2 pos)
    {
        RickshawSkins.Skin skin = RickshawSkins.All[index];

        RectTransform rt = MakeRect(parent, "Card_" + skin.id, new Vector2(0.5f, 0.5f), pos, new Vector2(230f, 270f));
        Image frame = rt.gameObject.AddComponent<Image>();
        frame.color = new Color(0.16f, 0.19f, 0.26f, 1f);
        Button btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = frame;
        btn.onClick.AddListener(() => SelectCard(index));

        RectTransform sw = MakeRect(rt, "Swatch", new Vector2(0.5f, 1f), new Vector2(0f, -15f), new Vector2(200f, 150f));
        Image swImg = sw.gameObject.AddComponent<Image>();
        swImg.color = skin.swatch;
        swImg.raycastTarget = false;

        RectTransform nm = MakeRect(rt, "Name", new Vector2(0.5f, 1f), new Vector2(0f, -170f), new Vector2(220f, 44f));
        MakeText(nm, skin.name, 26, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);

        RectTransform st = MakeRect(rt, "Status", new Vector2(0.5f, 1f), new Vector2(0f, -218f), new Vector2(220f, 44f));
        Text status = MakeText(st, "", 28, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);

        Card card = new Card();
        card.skin = skin;
        card.frame = frame;
        card.status = status;
        card.button = btn;
        cards.Add(card);
    }

    // pivot is the anchor; "anchored" position and size in reference pixels
    static RectTransform MakeRect(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, anchor.y == 1f ? 1f : 0.5f);
        if (anchor.x == 1f) rt.pivot = new Vector2(1f, rt.pivot.y);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    Text MakeText(RectTransform parent, string text, int size, FontStyle style, TextAnchor align, Color color)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(8f, 2f);
        rt.offsetMax = new Vector2(-8f, -2f);
        Text t = go.GetComponent<Text>();
        t.font = font;
        t.text = text;
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = align;
        t.color = color;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    // ---------- behaviour ----------

    public void OpenShop()
    {
        open = true;
        shopRoot.SetActive(true);
        openButton.SetActive(false);
        if (previewCam != null) previewCam.enabled = true;

        PlayerProfileData p = Profile;
        selected = 0;
        if (p != null)
        {
            for (int i = 0; i < RickshawSkins.All.Length; i++)
                if (RickshawSkins.All[i].id == (string.IsNullOrEmpty(p.equippedSkin) ? "classic" : p.equippedSkin)) selected = i;
        }
        messageText.text = p == null ? "Select a profile first to buy skins." : "";
        ApplyPreview();
        Refresh();
    }

    public void CloseShop()
    {
        open = false;
        shopRoot.SetActive(false);
        openButton.SetActive(true);
        if (previewCam != null) previewCam.enabled = false;
    }

    public void SelectCard(int index)
    {
        selected = index;
        messageText.text = "";
        ApplyPreview();
        Refresh();
    }

    void ApplyPreview()
    {
        if (previewModel != null)
            RickshawSkins.Apply(previewModel.transform, bodyMaterialName, RickshawSkins.All[selected]);
    }

    bool Owns(PlayerProfileData p, RickshawSkins.Skin skin)
    {
        if (skin.price == 0) return true;
        return p != null && p.ownedSkins != null && p.ownedSkins.Contains(skin.id);
    }

    // a skin the profile already owns stays usable, whatever its unlock level
    bool IsLocked(PlayerProfileData p, RickshawSkins.Skin skin)
    {
        return !Owns(p, skin) && RickshawSkins.IsLocked(skin, p != null ? p.levelsCompleted : 0);
    }

    bool IsEquipped(PlayerProfileData p, RickshawSkins.Skin skin)
    {
        string equipped = (p == null || string.IsNullOrEmpty(p.equippedSkin)) ? "classic" : p.equippedSkin;
        return skin.id == equipped;
    }

    void Refresh()
    {
        PlayerProfileData p = Profile;
        balanceText.text = p == null ? "no profile" : "$ " + p.money.ToString("N0");

        for (int i = 0; i < cards.Count; i++)
        {
            Card c = cards[i];
            c.frame.color = i == selected ? new Color(0.95f, 0.72f, 0.12f, 1f) : new Color(0.16f, 0.19f, 0.26f, 1f);
            if (IsEquipped(p, c.skin)) { c.status.text = "EQUIPPED"; c.status.color = new Color(0.4f, 1f, 0.5f, 1f); }
            else if (Owns(p, c.skin)) { c.status.text = "OWNED"; c.status.color = Color.white; }
            else if (IsLocked(p, c.skin)) { c.status.text = "LOCKED"; c.status.color = new Color(0.7f, 0.72f, 0.78f, 1f); }
            else { c.status.text = "$ " + c.skin.price; c.status.color = new Color(1f, 0.84f, 0.3f, 1f); }
            // text on the highlighted (yellow) card stays readable thanks to the dark text below
            if (i == selected && !IsEquipped(p, c.skin) && !Owns(p, c.skin)) c.status.color = new Color(0.25f, 0.15f, 0f, 1f);
            if (i == selected && Owns(p, c.skin) && !IsEquipped(p, c.skin)) c.status.color = new Color(0.1f, 0.1f, 0.1f, 1f);

            Transform swatch = c.frame.transform.Find("Swatch");
            if (swatch != null)
                swatch.GetComponent<Image>().color = IsLocked(p, c.skin) ? Color.Lerp(c.skin.swatch, new Color(0.1f, 0.1f, 0.12f, 1f), 0.75f) : c.skin.swatch;
        }

        RickshawSkins.Skin sel = RickshawSkins.All[selected];
        selectedName.text = sel.name;

        if (p == null)
        {
            SetAction("SELECT A PROFILE FIRST", new Color(0.35f, 0.35f, 0.4f, 1f), false);
        }
        else if (IsEquipped(p, sel))
        {
            SetAction("EQUIPPED", new Color(0.2f, 0.5f, 0.3f, 1f), false);
        }
        else if (Owns(p, sel))
        {
            SetAction("EQUIP", new Color(0.2f, 0.6f, 0.9f, 1f), true);
        }
        else if (IsLocked(p, sel))
        {
            string label = sel.unlockLevel == RickshawSkins.ComingSoon ? "COMING SOON" : "UNLOCK: BEAT LEVEL " + sel.unlockLevel;
            SetAction(label, new Color(0.35f, 0.35f, 0.4f, 1f), false);
        }
        else
        {
            bool canAfford = p.money >= sel.price;
            SetAction("BUY  -  $ " + sel.price, canAfford ? new Color(0.2f, 0.7f, 0.3f, 1f) : new Color(0.55f, 0.25f, 0.25f, 1f), true);
        }
    }

    void SetAction(string label, Color color, bool interactable)
    {
        actionText.text = label;
        actionImage.color = color;
        actionButton.interactable = interactable;
    }

    public void OnAction()
    {
        PlayerProfileData p = Profile;
        if (p == null) return;
        if (p.ownedSkins == null) p.ownedSkins = new List<string>();

        RickshawSkins.Skin sel = RickshawSkins.All[selected];
        if (IsEquipped(p, sel) || IsLocked(p, sel)) return;

        if (!Owns(p, sel))
        {
            if (p.money < sel.price)
            {
                messageText.text = "Not enough coins - you need $ " + sel.price + ". Earn more by driving customers!";
                return;
            }
            p.money -= sel.price;
            p.ownedSkins.Add(sel.id);
            messageText.text = "";
        }

        p.equippedSkin = sel.id;
        if (ProfileManager.Instance != null) ProfileManager.Instance.SaveActiveProfile();
        ProfileSelectionUI slotsUi = FindFirstObjectByType<ProfileSelectionUI>(FindObjectsInactive.Include);
        if (slotsUi != null) slotsUi.RefreshUI(); // keeps the money shown on the profile cards up to date
        Refresh();
    }

#if UNITY_EDITOR
    // Right-click this component's header > "Bake Shop Into Scene" to turn the runtime-built shop into real,
    // saved scene objects that you can move, resize and recolor in the Hierarchy / Inspector.
    // Run it again at any time to rebuild everything from the code defaults.
    [ContextMenu("Bake Shop Into Scene")]
    public void BakeIntoScene()
    {
        if (Application.isPlaying) { Debug.LogWarning("RickshawSkinShop: stop Play mode before baking."); return; }
        if (rickshawModelPrefab == null) { Debug.LogError("RickshawSkinShop: assign 'Rickshaw Model Prefab' first."); return; }

        foreach (string childName in new[] { "ItemShopCanvas", "SkinShopPreview" })
        {
            Transform old = transform.Find(childName);
            if (old != null) DestroyImmediate(old.gameObject);
        }

        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // the texture the preview camera draws into becomes a real asset
        if (!AssetDatabase.IsValidFolder("Assets/Arts/UI"))
        {
            if (!AssetDatabase.IsValidFolder("Assets/Arts")) AssetDatabase.CreateFolder("Assets", "Arts");
            AssetDatabase.CreateFolder("Assets/Arts", "UI");
        }
        const string rtPath = "Assets/Arts/UI/SkinShopPreview.renderTexture";
        RenderTexture rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(rtPath);
        if (rt == null)
        {
            rt = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGB32);
            AssetDatabase.CreateAsset(rt, rtPath);
            AssetDatabase.SaveAssets();
        }
        previewRT = rt;

        cards.Clear();
        previewModel = null;
        previewCam = null;
        baking = true;
        BuildPreview();
        BuildUi();
        baking = false;

        cardViews = new CardView[cards.Count];
        for (int i = 0; i < cards.Count; i++)
        {
            CardView view = new CardView();
            view.frame = cards[i].frame;
            view.status = cards[i].status;
            view.button = cards[i].button;
            cardViews[i] = view;
        }

        // click handlers that are saved in the scene (visible in each Button's "On Click" list)
        WirePersistent(openButton.GetComponent<Button>(), OpenShop);
        WirePersistent(closeButton, CloseShop);
        WirePersistent(actionButton, OnAction);
        for (int i = 0; i < cards.Count; i++)
        {
            Button b = cards[i].button;
            b.onClick.RemoveAllListeners();
            UnityEventTools.AddIntPersistentListener(b.onClick, SelectCard, i);
        }

        shopRoot.SetActive(false); // saved hidden, exactly like at runtime (tick it in the Inspector to edit it)
        EditorUtility.SetDirty(this);
        EditorSceneManager.MarkSceneDirty(gameObject.scene);
        Debug.Log("RickshawSkinShop: the shop is now real objects under '" + name + "'. Save the scene (Ctrl+S).");
    }

    static void WirePersistent(Button b, UnityAction action)
    {
        b.onClick.RemoveAllListeners();
        UnityEventTools.AddPersistentListener(b.onClick, action);
    }
#endif
}
