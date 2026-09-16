using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Parks this player's character sprite at a claimed slot on the shared
/// PlayerIconStage so the one scene-wide capture camera renders it, then
/// crops this player's own RawImage to just that slot's portion of the
/// shared RenderTexture. Purely local/visual - not networked, since every
/// client independently stages and crops the same characters the same way.
/// </summary>
public class PlayerIconCapture : MonoBehaviour
{
    [Tooltip("Root of the imported character sprite hierarchy - gets moved to this instance's claimed slot.")]
    [SerializeField] private Transform _characterRoot;
    [SerializeField] private RawImage _rawImage;

    private int _slotIndex = -1;

    private void Start()
    {
        if (!_characterRoot || !_rawImage)
        {
            Debug.LogError("PlayerIconCapture is missing a required reference.", this);
            return;
        }

        var stage = PlayerIconStage.instance;
        if (!stage)
        {
            Debug.LogError("PlayerIconCapture: no PlayerIconStage found in the scene.", this);
            return;
        }

        if (!stage.TryClaimSlot(out _slotIndex, out var slotTransform))
        {
            Debug.LogError("PlayerIconCapture: PlayerIconStage has no free slots left.", this);
            return;
        }

        _characterRoot.SetPositionAndRotation(slotTransform.position, slotTransform.rotation);
        _rawImage.texture = stage.sharedTexture;
        _rawImage.uvRect = stage.GetUvRectForSlot(_slotIndex);
    }

    private void OnDestroy()
    {
        if (_slotIndex >= 0 && PlayerIconStage.instance)
            PlayerIconStage.instance.ReleaseSlot(_slotIndex);
    }
}
