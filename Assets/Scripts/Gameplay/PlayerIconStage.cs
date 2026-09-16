using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One shared camera + RenderTexture captures every player's character
/// sprite in a single pass, each parked at its own fixed staging slot in
/// the world. Individual players crop out just their own portion of that
/// shared texture via RawImage.uvRect - see PlayerIconCapture.
///
/// Lives once in MainGame.unity. Set up the capture camera separately in
/// the Editor: Culling Mask = the dedicated layer all character sprites
/// live on, Clear Flags = Solid Color with alpha 0, Target Texture =
/// sharedTexture below, framed so all slots fit in view in a row.
/// </summary>
public class PlayerIconStage : MonoBehaviour
{
    public static PlayerIconStage instance { get; private set; }

    [Tooltip("Empty Transforms marking where each player's character sits for capture - arrange them left to right to match the capture camera's framing.")]
    [SerializeField] private Transform[] _slots;
    [SerializeField] private RenderTexture _sharedTexture;

    public RenderTexture sharedTexture => _sharedTexture;

    private readonly Queue<int> _freeSlots = new Queue<int>();

    private void Awake()
    {
        instance = this;

        for (int i = 0; i < _slots.Length; i++)
            _freeSlots.Enqueue(i);
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }

    public bool TryClaimSlot(out int slotIndex, out Transform slotTransform)
    {
        if (_freeSlots.Count == 0)
        {
            slotIndex = -1;
            slotTransform = null;
            return false;
        }

        slotIndex = _freeSlots.Dequeue();
        slotTransform = _slots[slotIndex];
        return true;
    }

    public void ReleaseSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= _slots.Length)
            return;

        _freeSlots.Enqueue(slotIndex);
    }

    /// <summary>Assumes slots are laid out in one even horizontal row across the camera's view.</summary>
    public Rect GetUvRectForSlot(int slotIndex)
    {
        float width = 1f / _slots.Length;
        return new Rect(slotIndex * width, 0f, width, 1f);
    }
}
