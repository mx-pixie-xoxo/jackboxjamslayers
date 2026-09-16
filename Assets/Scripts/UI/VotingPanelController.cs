using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visible only for audience members who haven't voted yet this turn. The
/// slider value is a purely local echo until Submit is pressed - nothing
/// about it is sent or synced before that, so nobody else ever sees it
/// mid-drag, and other clients never see it at all before the reveal.
///
/// If this player's own countdown runs out first, this auto-submits
/// wherever the slider currently sits - the server has no way to do this
/// itself, since it never knows an unsent slider value.
/// </summary>
public class VotingPanelController : MonoBehaviour
{
    [SerializeField] private GameObject _panelRoot;
    [SerializeField] private Slider _slider;
    [SerializeField] private Button _submitButton;
    [SerializeField] private TMP_Text _progressText;
    [SerializeField] private TMP_Text _yourPositionText;

    [Header("Timer")]
    [SerializeField] private TMP_Text _timerText;

    private RoundManager _round;
    private bool _hasVotedThisTurn;
    private float _countdown;
    private bool _timerRunning;

    private void Update()
    {
        if (_round == null)
        {
            TryBind();
            return;
        }

        if (_timerRunning)
        {
            _countdown -= Time.deltaTime;

            if (_timerText)
                _timerText.text = $"{Mathf.CeilToInt(Mathf.Max(0f, _countdown))}s";

            if (_countdown <= 0f)
            {
                _timerRunning = false;
                OnSubmit(); // auto-submit wherever the slider currently sits
            }
        }
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

        if (phase == RoundPhase.VotingOpen && !IsActivePlayer())
        {
            _countdown = _round.votingDuration;
            _timerRunning = true;
        }
        else
        {
            _timerRunning = false;
        }

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

        _timerRunning = false;
        _hasVotedThisTurn = true;
        _round.Rpc_SubmitVote(_slider ? _slider.value : 0.5f);
        Refresh();
    }

    private bool IsActivePlayer()
    {
        return _round.activePlayerId.value.HasValue &&
               RoundManager.TryGetLocalPlayerId(out var localId) &&
               _round.activePlayerId.value.Value == localId;
    }

    private void Refresh()
    {
        if (_round == null || !_panelRoot)
            return;

        bool show = _round.phase.value == RoundPhase.VotingOpen && !IsActivePlayer() && !_hasVotedThisTurn;
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
