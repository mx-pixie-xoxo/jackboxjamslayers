using System.Collections.Generic;
using PurrNet;
using TMPro;
using UnityEngine;

/// <summary>
// used to have the player character move in tandem with thier vote
/// Must like on the Player.prefab root
/// </summary>
[RequireComponent(typeof(NetworkIdentity))]
public class PlayerVoteIcon : MonoBehaviour
{
    private static readonly Dictionary<PlayerID, PlayerVoteIcon> ByOwner = new Dictionary<PlayerID, PlayerVoteIcon>();

    public static bool TryGetByOwner(PlayerID owner, out PlayerVoteIcon icon) => ByOwner.TryGetValue(owner, out icon);

    [SerializeField] private RectTransform _icon;
    [Tooltip("Child of _icon holding the actual visual sprite - only this gets flipped, so siblings like the name label don't mirror with it.")]
    [SerializeField] private RectTransform _sprite;
    [Tooltip("Normalized screen-widths per second the icon slides at.")]
    [SerializeField] private float _moveSpeed = 1.5f;
    [Tooltip("Keeps the icon from touching the very edge of the screen.")]
    [SerializeField, Range(0f, 0.49f)] private float _horizontalMargin = 0.05f;

    [Header("Name label - parent it under the icon so it moves along with it")]
    [SerializeField] private TMP_Text _nameText;

    [Header("Score delta - owner-only (everyone else's copy of this prefab never shows it), enabled/disabled for your own DOTween animation to react to")]
    [SerializeField] private TMP_Text _scoreDeltaText;

    [Header("Animator (same rig/parameter on every player prefab)")]
    [SerializeField] private Animator _animator;

    private static readonly int IsMovingHash = Animator.StringToHash("IsMoving");

    /// <summary>The RectTransform PodiumController repositions at game end</summary>
    public RectTransform icon => _icon;

    private NetworkIdentity _identity;
    private RoundManager _round;
    private float _targetX = 0.5f;
    private bool _ownerResolved;
    private bool _gameOver;

    private void Awake()
    {
        _identity = GetComponent<NetworkIdentity>();
    }

    private void Update()
    {
        if (_round == null)
        {
            TryBind();
            return;
        }

        if (!_ownerResolved && _identity.owner.HasValue)
            ResolveOwner();

        // Once the game is over( ronds are done), PodiumController takes over positioning
        // the top 3 icons directly
        if (_gameOver || !_icon)
            return;

        float currentX = _icon.anchorMin.x;
        float newX = Mathf.MoveTowards(currentX, _targetX, _moveSpeed * Time.deltaTime);
        bool isMoving = !Mathf.Approximately(newX, currentX);

        if (isMoving && _sprite)
        {
            bool movingRight = newX > currentX;
            _sprite.localEulerAngles = new Vector3(0f, movingRight ? 180f : 0f, 0f);
        }

        if (_animator)
            _animator.SetBool(IsMovingHash, isMoving);

        _icon.anchorMin = new Vector2(newX, _icon.anchorMin.y);
        _icon.anchorMax = new Vector2(newX, _icon.anchorMax.y);
    }

    private void TryBind()
    {
        _round = RoundManager.instance;
        if (_round == null)
            return;

        _round.onVoteRevealed += OnVoteRevealed;
        _round.onDisplayNameChanged += OnDisplayNameChanged;
        _round.onLocalScoreChanged += OnLocalScoreChanged;
        _round.phase.onChanged += OnPhaseChanged;
    }

    private void OnDestroy()
    {
        if (_ownerResolved && _identity.owner.HasValue &&
            ByOwner.TryGetValue(_identity.owner.Value, out var existing) && existing == this)
        {
            ByOwner.Remove(_identity.owner.Value);
        }

        if (_round == null)
            return;

        _round.onVoteRevealed -= OnVoteRevealed;
        _round.onDisplayNameChanged -= OnDisplayNameChanged;
        _round.onLocalScoreChanged -= OnLocalScoreChanged;
        _round.phase.onChanged -= OnPhaseChanged;
    }

    private void OnVoteRevealed(PlayerID voter, float position)
    {
        if (!_identity.owner.HasValue || _identity.owner.Value != voter)
            return;

        _targetX = Mathf.Lerp(_horizontalMargin, 1f - _horizontalMargin, Mathf.Clamp01(position));
    }

    private void OnDisplayNameChanged(PlayerID player, string displayName)
    {
        if (_identity.owner.HasValue && _identity.owner.Value == player && _nameText)
            _nameText.text = displayName;
    }

    private void ResolveOwner()
    {
        if (!_identity.owner.HasValue)
            return;

        _ownerResolved = true;
        ByOwner[_identity.owner.Value] = this;

        if (_nameText)
            _nameText.text = _round.GetDisplayName(_identity.owner.Value);
    }

    private void OnLocalScoreChanged(int delta, int newTotal)
    {
        //just changes the score of YOur players score, not anyone elses
        if (!_identity.isOwner || !_scoreDeltaText)
            return;

        _scoreDeltaText.text = $"+{delta}";
        _scoreDeltaText.gameObject.SetActive(true);
    }

    private void OnPhaseChanged(RoundPhase phase)
    {
        // Hide again once a fresh turn begins.
    
        if (phase == RoundPhase.Targeting && _scoreDeltaText)
            _scoreDeltaText.gameObject.SetActive(false);

        _gameOver = phase == RoundPhase.GameOver;
    }
}
