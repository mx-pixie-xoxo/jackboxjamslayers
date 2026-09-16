using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visible and interactable only for the active player, only during
/// Targeting. Everyone else's copy of this panel just stays hidden - the
/// secret goal text/marker are populated from RoundManager's local-only
/// event, so they only ever have data to show on the one client the goal
/// was actually sent to.
///
/// Choosing a side submits immediately - there's no separate Submit button.
/// Each side button is only interactable once the input field has text, and
/// both reset the moment this player becomes active for a new turn.
/// </summary>
public class TargetingPanelController : MonoBehaviour
{
    [SerializeField] private GameObject _panelRoot;
    [SerializeField] private TMP_InputField _labelInput;

    [Header("Choosing a side submits immediately")]
    [SerializeField] private Button _chooseLeftButton;
    [SerializeField] private TMP_Text _chooseLeftButtonText;
    [SerializeField] private Button _chooseRightButton;
    [SerializeField] private TMP_Text _chooseRightButtonText;

    [Header("Mean readout (0.5 = dead center, <0.5 = left, >0.5 = right)")]
    [SerializeField] private TMP_Text _currentMeanText;
    [SerializeField] private TMP_Text _targetInstructionText;

    [Header("Non-interactive mean indicator (optional)")]
    [Tooltip("A RectTransform whose anchorMin/anchorMax.x get driven to sit at the current pendulum value along a track.")]
    [SerializeField] private RectTransform _currentMeanMarker;
    [Tooltip("Same idea, driven to the midpoint of the active player's secret goal range.")]
    [SerializeField] private RectTransform _targetMeanMarker;

    [Header("Timer (display only - the server enforces the real timeout)")]
    [SerializeField] private TMP_Text _timerText;

    private const string CurrentMeanColorHex = "0000FF";
    private const string TargetMeanColorHex = "FF0000";

    private RoundManager _round;
    private bool _wasMyTurn;
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
            _countdown = Mathf.Max(0f, _countdown - Time.deltaTime);

            if (_timerText)
                _timerText.text = $"{Mathf.CeilToInt(_countdown)}s";

            if (_countdown <= 0f)
                _timerRunning = false; // just stops the display - the server owns the actual timeout
        }

        Refresh();
    }

    private void TryBind()
    {
        _round = RoundManager.instance;
        if (_round == null)
            return;

        _round.phase.onChanged += OnPhaseChanged;
        _round.activePlayerId.onChanged += OnActivePlayerChanged;
        _round.pendulumValue.onChanged += OnPendulumChanged;
        _round.leftLabel.onChanged += OnLeftLabelChanged;
        _round.rightLabel.onChanged += OnRightLabelChanged;
        _round.onLocalGoalReceived += OnLocalGoalReceived;

        if (_chooseLeftButton) _chooseLeftButton.onClick.AddListener(ChooseLeftClicked);
        if (_chooseRightButton) _chooseRightButton.onClick.AddListener(ChooseRightClicked);
        if (_labelInput) _labelInput.onValueChanged.AddListener(OnInputChanged);

        OnPendulumChanged(_round.pendulumValue.value);
        OnLeftLabelChanged(_round.leftLabel.value);
        OnRightLabelChanged(_round.rightLabel.value);
        OnInputChanged(_labelInput ? _labelInput.text : "");
        Refresh();
    }

    private void OnDisable()
    {
        if (_round == null)
            return;

        _round.phase.onChanged -= OnPhaseChanged;
        _round.activePlayerId.onChanged -= OnActivePlayerChanged;
        _round.pendulumValue.onChanged -= OnPendulumChanged;
        _round.leftLabel.onChanged -= OnLeftLabelChanged;
        _round.rightLabel.onChanged -= OnRightLabelChanged;
        _round.onLocalGoalReceived -= OnLocalGoalReceived;
        _round = null;
    }

    private void ChooseLeftClicked() => Choose(true);
    private void ChooseRightClicked() => Choose(false);

    private void Choose(bool isLeft)
    {
        if (_round == null || !_labelInput || string.IsNullOrWhiteSpace(_labelInput.text))
            return;

        _round.Rpc_SubmitEndpointLabel(isLeft, _labelInput.text);
    }

    private void OnInputChanged(string value)
    {
        bool hasText = !string.IsNullOrWhiteSpace(value);
        if (_chooseLeftButton) _chooseLeftButton.interactable = hasText;
        if (_chooseRightButton) _chooseRightButton.interactable = hasText;
    }

    private void OnPhaseChanged(RoundPhase phase) => Refresh();
    private void OnActivePlayerChanged(PurrNet.PlayerID? player) => Refresh();

    private void OnLeftLabelChanged(string value)
    {
        if (_chooseLeftButtonText)
            _chooseLeftButtonText.text = $"Replace \"{value}\"";
    }

    private void OnRightLabelChanged(string value)
    {
        if (_chooseRightButtonText)
            _chooseRightButtonText.text = $"Replace \"{value}\"";
    }

    private void OnPendulumChanged(float value)
    {
        if (_currentMeanText)
            _currentMeanText.text = $"The mean is {Colorize(FormatDirectional(value), CurrentMeanColorHex)}";

        PositionMarker(_currentMeanMarker, value);
    }

    private void OnLocalGoalReceived(float min, float max)
    {
        float target = (min + max) * 0.5f;

        if (_targetInstructionText)
            _targetInstructionText.text =
                $"Change one of the sides to make the mean {Colorize(FormatDirectional(target), TargetMeanColorHex)}";

        PositionMarker(_targetMeanMarker, target);
    }

    /// <summary>0.5 -> "dead center"; otherwise "{0-100}% to the left/right", 100% at either edge.</summary>
    private static string FormatDirectional(float normalized)
    {
        float percent = Mathf.Abs(normalized - 0.5f) * 200f;
        if (percent < 0.5f)
            return "dead center";

        string direction = normalized > 0.5f ? "right" : "left";
        return $"{percent:0}% to the {direction}";
    }

    /// <summary>Wraps text in a TMP rich-text color tag - requires Rich Text enabled on the TMP_Text (on by default).</summary>
    private static string Colorize(string text, string hexColor) => $"<color=#{hexColor}>{text}</color>";

    private static void PositionMarker(RectTransform marker, float normalized)
    {
        if (!marker)
            return;

        float clamped = Mathf.Clamp01(normalized);
        marker.anchorMin = new Vector2(clamped, marker.anchorMin.y);
        marker.anchorMax = new Vector2(clamped, marker.anchorMax.y);
    }

    private void Refresh()
    {
        if (_round == null || !_panelRoot)
            return;

        bool isMyTurn = _round.phase.value == RoundPhase.Targeting &&
                        _round.activePlayerId.value.HasValue &&
                        RoundManager.TryGetLocalPlayerId(out var localId) &&
                        _round.activePlayerId.value.Value == localId;

        if (isMyTurn && !_wasMyTurn)
        {
            _round.Rpc_RequestSecretGoal();
            ResetInput();
            _countdown = _round.targetingDuration;
            _timerRunning = true;
        }
        else if (!isMyTurn && _wasMyTurn)
        {
            _timerRunning = false;
        }

        _wasMyTurn = isMyTurn;
        _panelRoot.SetActive(isMyTurn);
    }

    private void ResetInput()
    {
        if (_labelInput)
            _labelInput.text = "";

        OnInputChanged("");
    }
}
