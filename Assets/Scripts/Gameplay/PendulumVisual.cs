using UnityEngine;

/// <summary>
/// Purely cosmetic - just reflects RoundManager.pendulumValue. Replaces the
/// old empty PendulumScript.cs stub; the pendulum's actual value lives on
/// RoundManager since it doesn't need its own networked object.
/// </summary>
public class PendulumVisual : MonoBehaviour
{
    [SerializeField] private Transform _pivot;
    [SerializeField] private float _swingRangeDegrees = 45f;

    private RoundManager _round;

    private void Update()
    {
        if (_round == null)
        {
            _round = RoundManager.instance;
            if (_round == null)
                return;

            _round.pendulumValue.onChanged += OnPendulumChanged;
            OnPendulumChanged(_round.pendulumValue.value);
        }
    }

    private void OnDestroy()
    {
        if (_round != null)
            _round.pendulumValue.onChanged -= OnPendulumChanged;
    }

    private void OnPendulumChanged(float normalized)
    {
        if (!_pivot)
            return;

        float angle = Mathf.Lerp(-_swingRangeDegrees, _swingRangeDegrees, normalized);
        _pivot.localRotation = Quaternion.Euler(0f, 0f, angle);
    }
}
