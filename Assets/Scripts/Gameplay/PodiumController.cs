using PurrNet;
using UnityEngine;

/// <summary>
/// Scene singleton holding the 3 podium slot positions (screen-space
/// RectTransforms). At the end, moves the top 3 players' own icons
/// (PlayerVoteIcon.icon) onto these
/// slots, and enables the podium's own visuals at the same time.
///
/// onyl issue is that if someone elaves thier player dissapears too
/// </summary>
public class PodiumController : MonoBehaviour
{
    public static PodiumController instance { get; private set; }

    [SerializeField] private RectTransform _firstPlaceSlot;
    [SerializeField] private RectTransform _secondPlaceSlot;
    [SerializeField] private RectTransform _thirdPlaceSlot;

    [Tooltip("Enabled only once the game reaches GameOver - the podium model/scenery itself, kept hidden the rest of the game.")]
    [SerializeField] private GameObject _podiumRoot;

    private RoundManager _round;
    private PlayerID? _first, _second, _third;
    private bool _rankingKnown;
    private bool _activated;

    private void Awake()
    {
        instance = this;

        if (_podiumRoot)
            _podiumRoot.SetActive(false);
    }

    private void Update()
    {
        if (_round == null)
        {
            _round = RoundManager.instance;
            if (_round == null)
                return;

            _round.phase.onChanged += OnPhaseChanged;
            _round.onPodiumRevealed += OnPodiumRevealed;
            OnPhaseChanged(_round.phase.value);
        }
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;

        if (_round == null)
            return;

        _round.phase.onChanged -= OnPhaseChanged;
        _round.onPodiumRevealed -= OnPodiumRevealed;
    }

    private void OnPodiumRevealed(PlayerID? first, PlayerID? second, PlayerID? third)
    {
        _first = first;
        _second = second;
        _third = third;
        _rankingKnown = true;

        TryActivate();
    }

    private void OnPhaseChanged(RoundPhase phase)
    {
        if (phase == RoundPhase.GameOver)
        {
            TryActivate();
            return;
        }

        _activated = false;
        _rankingKnown = false;

        if (_podiumRoot)
            _podiumRoot.SetActive(false);
    }

    private void TryActivate()
    {
        if (_activated || !_rankingKnown || _round == null || _round.phase.value != RoundPhase.GameOver)
            return;

        _activated = true;

        if (_podiumRoot)
            _podiumRoot.SetActive(true);

        PlacePlayerAt(_first, _firstPlaceSlot);
        PlacePlayerAt(_second, _secondPlaceSlot);
        PlacePlayerAt(_third, _thirdPlaceSlot);
    }

    private static void PlacePlayerAt(PlayerID? player, RectTransform slot)
    {
        if (!player.HasValue || !slot)
            return;

        if (!PlayerVoteIcon.TryGetByOwner(player.Value, out var playerIcon) || !playerIcon.icon)
            return;

        var icon = playerIcon.icon;
        icon.anchorMin = slot.anchorMin;
        icon.anchorMax = slot.anchorMax;
        icon.pivot = slot.pivot;
        icon.anchoredPosition = slot.anchoredPosition;
        icon.sizeDelta = slot.sizeDelta;
    }
}
