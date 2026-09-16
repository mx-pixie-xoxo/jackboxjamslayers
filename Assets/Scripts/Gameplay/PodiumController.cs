using UnityEngine;

/// <summary>
/// Scene singleton holding the 3 podium slot positions. Each player's own
/// PlayerIconCapture checks the podium ranking itself and queries this
/// directly for its slot when it's in the top 3 - keeping the "am I 1st,
/// 2nd, or 3rd" identity check where the player-ownership data already
/// lives, rather than this class searching the scene for players.
/// </summary>
public class PodiumController : MonoBehaviour
{
    public static PodiumController instance { get; private set; }

    [SerializeField] private Transform _firstPlaceSlot;
    [SerializeField] private Transform _secondPlaceSlot;
    [SerializeField] private Transform _thirdPlaceSlot;

    public Transform firstPlaceSlot => _firstPlaceSlot;
    public Transform secondPlaceSlot => _secondPlaceSlot;
    public Transform thirdPlaceSlot => _thirdPlaceSlot;

    private void Awake()
    {
        instance = this;
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }
}
