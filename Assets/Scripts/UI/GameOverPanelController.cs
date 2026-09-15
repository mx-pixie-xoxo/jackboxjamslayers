using System.Collections.Generic;
using PurrNet;
using TMPro;
using UnityEngine;

/// <summary>
/// Hidden for the entire game. This is the *only* screen that ever shows
/// more than one player's score - populated once, at game over, from the
/// server's single explicit reveal-all broadcast.
/// </summary>
public class GameOverPanelController : MonoBehaviour
{
    [SerializeField] private GameObject _panelRoot;
    [SerializeField] private TMP_Text _leaderboardText;

    private RoundManager _round;
    private readonly List<string> _lines = new List<string>();

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
        _round.onFinalScoreRevealed += OnFinalScoreRevealed;

        OnPhaseChanged(_round.phase.value);
    }

    private void OnDisable()
    {
        if (_round == null)
            return;

        _round.phase.onChanged -= OnPhaseChanged;
        _round.onFinalScoreRevealed -= OnFinalScoreRevealed;
        _round = null;
    }

    private void OnPhaseChanged(RoundPhase phase)
    {
        if (_panelRoot)
            _panelRoot.SetActive(phase == RoundPhase.GameOver);
    }

    private void OnFinalScoreRevealed(PlayerID player, int finalScore)
    {
        _lines.Add($"{player}: {finalScore}");
        if (_leaderboardText)
            _leaderboardText.text = string.Join("\n", _lines);
    }
}
