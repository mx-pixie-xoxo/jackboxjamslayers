using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visible and interactable only for the active player, only during
/// Targeting. Everyone else's copy of this panel just stays hidden - the
/// secret goal text is populated from RoundManager's local-only event, so
/// it only ever has data to show on the one client it was sent to.
/// </summary>
public class TargetingPanelController : MonoBehaviour
{
    [SerializeField] private GameObject _panelRoot;
    [SerializeField] private TMP_InputField _labelInput;
    [SerializeField] private Button _chooseLeftButton;
    [SerializeField] private Button _chooseRightButton;
    [SerializeField] private Button _submitButton;
    [SerializeField] private TMP_Text _secretGoalText;

    private RoundManager _round;
    private bool _isLeft = true;

    private void Update()
    {
        if (_round == null)
            TryBind();
        else
            Refresh();
    }

    private void TryBind()
    {
        _round = RoundManager.instance;
        if (_round == null)
            return;

        _round.phase.onChanged += OnPhaseChanged;
        _round.activePlayerId.onChanged += OnActivePlayerChanged;
        _round.onLocalGoalReceived += OnLocalGoalReceived;

        if (_chooseLeftButton) _chooseLeftButton.onClick.AddListener(ChooseLeft);
        if (_chooseRightButton) _chooseRightButton.onClick.AddListener(ChooseRight);
        if (_submitButton) _submitButton.onClick.AddListener(OnSubmit);

        Refresh();
    }

    private void OnDisable()
    {
        if (_round == null)
            return;

        _round.phase.onChanged -= OnPhaseChanged;
        _round.activePlayerId.onChanged -= OnActivePlayerChanged;
        _round.onLocalGoalReceived -= OnLocalGoalReceived;
        _round = null;
    }

    private void ChooseLeft() => _isLeft = true;
    private void ChooseRight() => _isLeft = false;

    private void OnPhaseChanged(RoundPhase phase) => Refresh();
    private void OnActivePlayerChanged(PurrNet.PlayerID? player) => Refresh();

    private void OnLocalGoalReceived(float min, float max)
    {
        if (_secretGoalText)
            _secretGoalText.text = $"Secret goal: {min:0.00} - {max:0.00}";
    }

    private void Refresh()
    {
        if (_round == null || !_panelRoot)
            return;

        bool isMyTurn = _round.phase.value == RoundPhase.Targeting &&
                        _round.activePlayerId.value.HasValue &&
                        RoundManager.TryGetLocalPlayerId(out var localId) &&
                        _round.activePlayerId.value.Value == localId;

        _panelRoot.SetActive(isMyTurn);
    }

    private void OnSubmit()
    {
        if (_round == null || !_labelInput)
            return;

        _round.Rpc_SubmitEndpointLabel(_isLeft, _labelInput.text);
    }
}
