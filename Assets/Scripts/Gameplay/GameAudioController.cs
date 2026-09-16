using UnityEngine;

/// <summary>
/// Background music and one-shot audio cues, all driven off RoundManager's
/// existing phase/event signals. Purely local presentation, not networked -
/// every client runs its own copy and reacts only to data it already
/// legitimately has. The "your turn" cue is private to the active player
/// for the same reason the rest of that UI is: it's driven by
/// onLocalGoalReceived, which only ever fires on the one client the secret
/// goal was actually sent to - this script does no privacy filtering of
/// its own.
/// </summary>
public class GameAudioController : MonoBehaviour
{
    [Header("Music (looping - one plays at a time, carries over between turns until the other phase starts)")]
    [SerializeField] private AudioSource _musicSource;
    [SerializeField] private AudioClip _targetingMusic;
    [SerializeField] private AudioClip _votingMusic;

    [Header("One-shot cues")]
    [SerializeField] private AudioSource _sfxSource;
    [Tooltip("Plays only for the active player, the moment it becomes their turn to input a word.")]
    [SerializeField] private AudioClip _yourTurnClip;
    [Tooltip("Plays only for audience members (not the active player), a set number of seconds before voting closes.")]
    [SerializeField] private AudioClip _votingWarningClip;
    [SerializeField] private float _votingWarningSecondsBeforeEnd = 10f;
    [Tooltip("Plays for everyone the moment voting closes (whether by everyone submitting or by timeout).")]
    [SerializeField] private AudioClip _votingEndedClip;
    [Tooltip("Plays for everyone right before the pendulum starts moving.")]
    [SerializeField] private AudioClip _pendulumCueClip;

    private RoundManager _round;
    private bool _votingWarningPlayedThisTurn;
    private float _votingCountdown;
    private bool _votingCountdownRunning;
    private float _lastYourTurnGoalMin = float.NaN;
    private float _lastYourTurnGoalMax = float.NaN;

    private void Update()
    {
        if (_round == null)
        {
            TryBind();
            return;
        }

        if (!_votingCountdownRunning)
            return;

        _votingCountdown -= Time.deltaTime;

        if (!_votingWarningPlayedThisTurn && _votingCountdown <= _votingWarningSecondsBeforeEnd)
        {
            _votingWarningPlayedThisTurn = true;
            PlaySfx(_votingWarningClip);
        }
    }

    private void TryBind()
    {
        _round = RoundManager.instance;
        if (_round == null)
            return;

        _round.phase.onChanged += OnPhaseChanged;
        _round.onLocalGoalReceived += OnLocalGoalReceived;
        _round.onVotingEnded += OnVotingEnded;
    }

    private void OnDestroy()
    {
        if (_round == null)
            return;

        _round.phase.onChanged -= OnPhaseChanged;
        _round.onLocalGoalReceived -= OnLocalGoalReceived;
        _round.onVotingEnded -= OnVotingEnded;
    }

    private void OnPhaseChanged(RoundPhase phase)
    {
        switch (phase)
        {
            case RoundPhase.Targeting:
                PlayMusic(_targetingMusic);
                break;
            case RoundPhase.VotingOpen:
                PlayMusic(_votingMusic);
                _votingWarningPlayedThisTurn = false;
                _votingCountdown = _round.votingDuration;
                _votingCountdownRunning = !IsActivePlayer(); // the active player isn't voting - no warning for them
                break;
            case RoundPhase.PendulumCue:
                _votingCountdownRunning = false;
                PlaySfx(_pendulumCueClip);
                break;
            default:
                _votingCountdownRunning = false;
                break;
        }
    }

    private void OnLocalGoalReceived(float min, float max)
    {
        // This can fire more than once per turn (the targeting panel
        // defensively re-requests it) - de-dupe on the goal range itself
        // rather than RoundManager.turnNumber. That SyncVar and this RPC
        // are delivered independently, and on a real (non-loopback)
        // connection can apply out of order relative to each other, which
        // let a duplicate slip through when comparing against turnNumber.
        // The push and the defensive re-request always carry the identical
        // (min, max) for a given turn, so comparing the payload itself
        // isn't vulnerable to that ordering hazard.
        bool isDuplicate = !float.IsNaN(_lastYourTurnGoalMin) &&
                           Mathf.Approximately(_lastYourTurnGoalMin, min) &&
                           Mathf.Approximately(_lastYourTurnGoalMax, max);

        if (isDuplicate)
            return;

        _lastYourTurnGoalMin = min;
        _lastYourTurnGoalMax = max;
        PlaySfx(_yourTurnClip);
    }

    private void OnVotingEnded()
    {
        _votingCountdownRunning = false;
        PlaySfx(_votingEndedClip);
    }

    private bool IsActivePlayer()
    {
        return _round.activePlayerId.value.HasValue &&
               RoundManager.TryGetLocalPlayerId(out var localId) &&
               _round.activePlayerId.value.Value == localId;
    }

    private void PlayMusic(AudioClip clip)
    {
        if (!_musicSource || _musicSource.clip == clip)
            return;

        _musicSource.clip = clip;

        if (clip)
            _musicSource.Play();
        else
            _musicSource.Stop();
    }

    private void PlaySfx(AudioClip clip)
    {
        if (_sfxSource && clip)
            _sfxSource.PlayOneShot(clip);
    }
}
