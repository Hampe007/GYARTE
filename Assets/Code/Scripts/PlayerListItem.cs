using UnityEngine;
using UnityEngine.UI;
using Steamworks;
using TMPro;

public class PlayerListItem : MonoBehaviour
{
    
    public string PlayerName;
    public int connectionID;
    public ulong PlayerSteamID;
    private bool avatarReceived;

    public TMP_Text playerNameText;
    public RawImage playerIcon;
    public TMP_Text playerReadyText;
    public bool ready;
    
    
    protected Callback<AvatarImageLoaded_t> imageLoaded;
    private Button playerIconButton;

    public void ChangeReadyStatus()
    {
        if (ready) {
            playerReadyText.text = "Ready";
            playerReadyText.color = Color.green;
            Debug.Log($"[UI] Row '{PlayerName}' is READY.");
        } else {
            playerReadyText.text = "Unready";
            playerReadyText.color = Color.red;
            Debug.Log($"[UI] Row '{PlayerName}' is UNREADY.");
        }
    }
    
    private void Awake()
    {
        imageLoaded = Callback<AvatarImageLoaded_t>.Create(OnImageLoaded);
        WirePlayerIconClickHandler();
    }

    private void OnDestroy()
    {
        if (playerIconButton != null)
        {
            playerIconButton.onClick.RemoveListener(OpenPlayerSteamProfile);
        }
    }

    private void GetPlayerIcon()
    {
        if (PlayerSteamID == 0 || playerIcon == null) return;
        
        int imageID = SteamFriends.GetLargeFriendAvatar(((CSteamID)PlayerSteamID));
        
        // -1: still loading (wait for OnImageLoaded)
        if (imageID == -1) return;

        // 0: error / no avatar
        if (imageID == 0) return;

        // We have an image now
        var tex = GetSteamImageAsTexture(imageID);
        if (tex != null)
        {
            playerIcon.texture = tex;
            avatarReceived = true;
        }
    }

    public void SetPlayerValues()
    {
        if (playerNameText != null)
            playerNameText.text = PlayerName;
        if (!avatarReceived) // if we haven't received the avatar yet, get it.
            GetPlayerIcon();
        ChangeReadyStatus();
    }

    private void WirePlayerIconClickHandler()
    {
        if (playerIcon == null) return;

        playerIconButton = playerIcon.GetComponent<Button>();
        if (playerIconButton == null) return;

        playerIconButton.onClick.RemoveListener(OpenPlayerSteamProfile);
        playerIconButton.onClick.AddListener(OpenPlayerSteamProfile);
    }

    private void OpenPlayerSteamProfile()
    {
        if (PlayerSteamID == 0)
        {
            Debug.LogWarning("[UI] Cannot open Steam profile: missing SteamID.");
            return;
        }

        if (!SteamManager.Initialized)
        {
            Debug.LogWarning("[UI] Cannot open Steam profile: Steam is not initialized.");
            return;
        }

        SteamFriends.ActivateGameOverlayToUser("steamid", new CSteamID(PlayerSteamID));
    }
    
    private Texture2D GetSteamImageAsTexture(int iImage)
    {
        Texture2D texture = null;

        bool isValid = SteamUtils.GetImageSize(iImage, out uint width, out uint height);
        if (isValid)
        {
            byte[] image = new byte[width * height * 4];

            isValid = SteamUtils.GetImageRGBA(iImage, image, (int)(width * height * 4));

            if (isValid)
            {
                // Flip vertically: Steam is bottom-up, Unity expects top-down
                byte[] flipped = new byte[image.Length];
                int rowSize = (int)width * 4;
                for (int y = 0; y < height; y++)
                {
                    int src = y * rowSize;
                    int dst = ((int)height - 1 - y) * rowSize;
                    System.Buffer.BlockCopy(image, src, flipped, dst, rowSize);
                }

                texture = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false, false);
                texture.LoadRawTextureData(flipped);
                texture.Apply();
            }
        }
        return texture;
    }
    
    private void OnImageLoaded(AvatarImageLoaded_t callback)
    {
        // keep your original pattern & variable name
        if (callback.m_steamID.m_SteamID != PlayerSteamID) return;

        var tex = GetSteamImageAsTexture(callback.m_iImage);
        if (tex != null)
        {
            playerIcon.texture = tex;
            avatarReceived = true;
        }
    }
}
