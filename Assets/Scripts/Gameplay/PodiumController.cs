using UnityEngine;

/// <summary>
/// Scene singleton holding the 3 podium slot positions. Each player's own
/// PlayerIconCapture checks the podium ranking itself and queries this
/// directly for its slot when it's in the top 3 - keeping the "am I 1st,
/// 2nd, or 3rd" identity check where the player-ownership data already
/// lives, rather than this class searching the scene for players.
///
/// Also enables/disables the podium's own visuals (model, lighting, etc.)
/// based on RoundManager's phase - hidden for the whole game, shown only
/// once it reaches GameOver.
/// </summary>
public class PodiumController : MonoBehaviour
{
    public static PodiumController instance { get; private set; }

    [SerializeField] private Transform _firstPlaceSlot;
    [SerializeField] private Transform _secondPlaceSlot;
    [SerializeField] private Transform _thirdPlaceSlot;

    [Tooltip("Enabled only once the game reaches GameOver - the podium model/scenery itself, kept hidden the rest of the game.")]
    [SerializeField] private GameObject _podiumRoot;

    public Transform firstPlaceSlot => _firstPlaceSlot;
    public Transform secondPlaceSlot => _secondPlaceSlot;
    public Transform thirdPlaceSlot => _thirdPlaceSlot;

    private RoundManager _round;

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
            OnPhaseChanged(_round.phase.value);
        }
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;

        if (_round != null)
            _round.phase.onChanged -= OnPhaseChanged;
    }

    private void OnPhaseChanged(RoundPhase phase)
    {
        if (_podiumRoot)
            _podiumRoot.SetActive(phase == RoundPhase.GameOver);
    }
}
