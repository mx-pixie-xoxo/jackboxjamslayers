using UnityEngine;

/// <summary>
/// Reflects the RoundManager.pendulumValue, striaght up lerping it. 
/// </summary>
public class PendulumVisual : MonoBehaviour
{
    [SerializeField] private Transform _pivot;
    [SerializeField] private float _swingRangeDegrees = 45f;
    [SerializeField] private float _swingAnimationDuration = 2f;

    private RoundManager _round;
    private float _fromValue = 0.5f;
    private float _toValue = 0.5f;
    private float _animElapsed;
    private bool _animating;

    private void Update()
    {
        if (_round == null)
        {
            _round = RoundManager.instance;
            if (_round == null)
                return;

            _round.pendulumValue.onChanged += OnPendulumChanged;

            // Snap to whatever the value already is on first bind (e.g. a
            // late join) - only future changes should actually animate.
            _fromValue = _toValue = _round.pendulumValue.value;
            ApplyAngle(_toValue);
            return;
        }

        if (!_animating)
            return;

        _animElapsed += Time.deltaTime;
        float t = _swingAnimationDuration > 0f ? Mathf.Clamp01(_animElapsed / _swingAnimationDuration) : 1f;
        ApplyAngle(Mathf.Lerp(_fromValue, _toValue, t));

        if (t >= 1f)
            _animating = false;
    }

    private void OnDestroy()
    {
        if (_round != null)
            _round.pendulumValue.onChanged -= OnPendulumChanged;
    }

    private void OnPendulumChanged(float normalized)
    {
        // Start from wherever the swing visually is right now (not
        // necessarily _toValue, if this fires again mid-animation) so a
        // re-trigger blends smoothly instead of jumping.
        _fromValue = CurrentAnimatedValue();
        _toValue = normalized;
        _animElapsed = 0f;
        _animating = true;
    }

    private float CurrentAnimatedValue()
    {
        float t = _swingAnimationDuration > 0f ? Mathf.Clamp01(_animElapsed / _swingAnimationDuration) : 1f;
        return Mathf.Lerp(_fromValue, _toValue, t);
    }

    private void ApplyAngle(float normalized)
    {
        if (!_pivot)
            return;

        float angle = Mathf.Lerp(-_swingRangeDegrees, _swingRangeDegrees, normalized);
        _pivot.localRotation = Quaternion.Euler(0f, 0f, angle);
    }
}
