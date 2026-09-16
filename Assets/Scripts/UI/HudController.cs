using PurrNet;
using TMPro;
using UnityEngine;

/// <summary>
/// Always-on HUD bits. "Your score" is bound only to this client's own
/// local score-changed event - it never has access to anyone else's.
/// </summary>
public class HudController : MonoBehaviour
{
    [SerializeField] private TMP_Text _roundText;
    [SerializeField] private TMP_Text _promptText;
    [SerializeField] private TMP_Text _leftLabelText;
    [SerializeField] private TMP_Text _rightLabelText;
    [SerializeField] private TMP_Text _activePlayerText;
    [SerializeField] private TMP_Text _yourScoreText;
    [SerializeField] private GameObject _waitingForPlayersPanel;

    private RoundManager _round;

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
        _round.roundNumber.onChanged += OnRoundCountChanged;
        _round.totalRounds.onChanged += OnRoundCountChanged;
        _round.promptText.onChanged += OnPromptChanged;
        _round.leftLabel.onChanged += OnLeftLabelChanged;
        _round.rightLabel.onChanged += OnRightLabelChanged;
        _round.activePlayerId.onChanged += OnActivePlayerChanged;
        _round.onLocalScoreChanged += OnLocalScoreChanged;
        _round.onDisplayNameChanged += OnDisplayNameChanged;

        OnPhaseChanged(_round.phase.value);
        UpdateRoundText();
        OnPromptChanged(_round.promptText.value);
        OnLeftLabelChanged(_round.leftLabel.value);
        OnRightLabelChanged(_round.rightLabel.value);
        OnActivePlayerChanged(_round.activePlayerId.value);
    }

    private void OnDisable()
    {
        if (_round == null)
            return;

        _round.phase.onChanged -= OnPhaseChanged;
        _round.roundNumber.onChanged -= OnRoundCountChanged;
        _round.totalRounds.onChanged -= OnRoundCountChanged;
        _round.promptText.onChanged -= OnPromptChanged;
        _round.leftLabel.onChanged -= OnLeftLabelChanged;
        _round.rightLabel.onChanged -= OnRightLabelChanged;
        _round.activePlayerId.onChanged -= OnActivePlayerChanged;
        _round.onLocalScoreChanged -= OnLocalScoreChanged;
        _round.onDisplayNameChanged -= OnDisplayNameChanged;
        _round = null;
    }

    private void OnPhaseChanged(RoundPhase phase)
    {
        if (_waitingForPlayersPanel)
            _waitingForPlayersPanel.SetActive(phase == RoundPhase.WaitingForPlayers);
    }

    private void OnRoundCountChanged(int _) => UpdateRoundText();

    private void UpdateRoundText()
    {
        if (_roundText)
            _roundText.text = $"Round {_round.roundNumber.value}/{_round.totalRounds.value}";
    }

    private void OnPromptChanged(string value)
    {
        if (_promptText) _promptText.text = value;
    }

    private void OnLeftLabelChanged(string value)
    {
        if (_leftLabelText) _leftLabelText.text = value;
    }

    private void OnRightLabelChanged(string value)
    {
        if (_rightLabelText) _rightLabelText.text = value;
    }

    private void OnActivePlayerChanged(PlayerID? player)
    {
        if (!_activePlayerText)
            return;

        _activePlayerText.text = player.HasValue ? $"Active player: {_round.GetDisplayName(player.Value)}" : "";
    }

    private void OnDisplayNameChanged(PlayerID player, string displayName)
    {
        // The active player's name can resolve after we've already shown
        // their (fallback-formatted) PlayerID - refresh once it arrives.
        if (_round.activePlayerId.value == player)
            OnActivePlayerChanged(_round.activePlayerId.value);
    }

    private void OnLocalScoreChanged(int delta, int newTotal)
    {
        if (_yourScoreText)
            _yourScoreText.text = $"Your score: {newTotal}";
    }
}
