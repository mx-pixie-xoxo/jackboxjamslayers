using System.Collections.Generic;
using PurrNet;
using TMPro;
using UnityEngine;

/// <summary>
/// Shown to everyone once voting closes. Populated entirely from
/// RoundManager's local reveal events - every client receives the same
/// broadcast at the same time, so this is the one screen where votes
/// (but never the secret goal range) become visible to everyone at once.
/// </summary>
public class RevealPanelController : MonoBehaviour
{
    [SerializeField] private GameObject _panelRoot;
    [SerializeField] private TMP_Text _pendulumText;
    [SerializeField] private TMP_Text _votesListText;

    private RoundManager _round;
    private readonly List<string> _voteLines = new List<string>();

    private void Update()
    {
        if (_round == null)
            TryBind();
    }

    private void TryBind()
    {
        _round = RoundManager.instance;
        if (_round == null)
            return;

        _round.phase.onChanged += OnPhaseChanged;
        _round.pendulumValue.onChanged += OnPendulumChanged;
        _round.onVoteRevealed += OnVoteRevealed;

        OnPhaseChanged(_round.phase.value);
        OnPendulumChanged(_round.pendulumValue.value);
    }

    private void OnDisable()
    {
        if (_round == null)
            return;

        _round.phase.onChanged -= OnPhaseChanged;
        _round.pendulumValue.onChanged -= OnPendulumChanged;
        _round.onVoteRevealed -= OnVoteRevealed;
        _round = null;
    }

    private void OnPhaseChanged(RoundPhase phase)
    {
        bool show = phase == RoundPhase.RevealMoving || phase == RoundPhase.PendulumCue ||
                    phase == RoundPhase.PendulumMoving || phase == RoundPhase.Scoring ||
                    phase == RoundPhase.TurnComplete;

        if (_panelRoot)
            _panelRoot.SetActive(show);

        if (phase == RoundPhase.Targeting)
        {
            _voteLines.Clear();
            if (_votesListText) _votesListText.text = "";
        }
    }

    private void OnPendulumChanged(float value)
    {
        if (_pendulumText)
            _pendulumText.text = $"Pendulum: {value:0.00}";
    }

    private void OnVoteRevealed(PlayerID voter, float position)
    {
        _voteLines.Add($"{_round.GetDisplayName(voter)}: {position:0.00}");
        if (_votesListText)
            _votesListText.text = string.Join("\n", _voteLines);
    }
}
