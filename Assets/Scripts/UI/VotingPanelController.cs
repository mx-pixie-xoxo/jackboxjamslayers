using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visible only for audience members who haven't voted yet this turn. The
/// slider value is a purely local echo until Submit is pressed - nothing
/// about it is sent or synced before that, so nobody else ever sees it
/// mid-drag, and other clients never see it at all before the reveal.
/// </summary>
public class VotingPanelController : MonoBehaviour
{
    [SerializeField] private GameObject _panelRoot;
    [SerializeField] private Slider _slider;
    [SerializeField] private Button _submitButton;
    [SerializeField] private TMP_Text _progressText;
    [SerializeField] private TMP_Text _yourPositionText;

    private RoundManager _round;
    private bool _hasVotedThisTurn;

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
        _round.votesSubmittedCount.onChanged += OnProgressChanged;
        _round.votesExpectedCount.onChanged += OnProgressChanged;
        _round.activePlayerId.onChanged += OnActivePlayerChanged;

        if (_slider) _slider.onValueChanged.AddListener(OnSliderChanged);
        if (_submitButton) _submitButton.onClick.AddListener(OnSubmit);

        Refresh();
    }

    private void OnDisable()
    {
        if (_round == null)
            return;

        _round.phase.onChanged -= OnPhaseChanged;
        _round.votesSubmittedCount.onChanged -= OnProgressChanged;
        _round.votesExpectedCount.onChanged -= OnProgressChanged;
        _round.activePlayerId.onChanged -= OnActivePlayerChanged;
        _round = null;
    }

    private void OnPhaseChanged(RoundPhase phase)
    {
        if (phase == RoundPhase.Targeting)
            _hasVotedThisTurn = false;

        Refresh();
    }

    private void OnActivePlayerChanged(PurrNet.PlayerID? player) => Refresh();
    private void OnProgressChanged(int _) => UpdateProgressText();

    private void OnSliderChanged(float value)
    {
        if (_yourPositionText)
            _yourPositionText.text = $"Your vote: {value:0.00}";
    }

    private void OnSubmit()
    {
        if (_round == null || _hasVotedThisTurn)
            return;

        _hasVotedThisTurn = true;
        _round.Rpc_SubmitVote(_slider ? _slider.value : 0.5f);
        Refresh();
    }

    private void Refresh()
    {
        if (_round == null || !_panelRoot)
            return;

        bool isActivePlayer = _round.activePlayerId.value.HasValue &&
                              RoundManager.TryGetLocalPlayerId(out var localId) &&
                              _round.activePlayerId.value.Value == localId;

        bool show = _round.phase.value == RoundPhase.VotingOpen && !isActivePlayer && !_hasVotedThisTurn;
        _panelRoot.SetActive(show);

        UpdateProgressText();
    }

    private void UpdateProgressText()
    {
        if (_round == null || !_progressText)
            return;

        _progressText.text = $"{_round.votesSubmittedCount.value}/{_round.votesExpectedCount.value} votes in";
    }
}
