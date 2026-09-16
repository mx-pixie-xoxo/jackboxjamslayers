using UnityEngine;

/// <summary>
/// Background music and one-shot audio cues, all driven off RoundManager's
/// existing phase/event signals. 
/// </summary>
public class GameAudioController : MonoBehaviour
{
    [Header("Music (looping - one plays at a time, carries over between turns until the other phase starts)")]
    [SerializeField] private AudioSource _musicSource;
    [SerializeField] private AudioClip _targetingMusic;
    [SerializeField] private AudioClip _votingMusic;

    [Header("One-shot cues (momentary events, everyone, never interrupted)")]
    [SerializeField] private AudioSource _sfxSource;
    [Tooltip("Plays for everyone the moment voting closes (whether by everyone submitting or by timeout).")]
    [SerializeField] private AudioClip _votingEndedClip;
    [Tooltip("Plays for everyone right before the pendulum starts moving.")]
    [SerializeField] private AudioClip _pendulumCueClip;

    [Header("Interruptible per-player cues (represent a waiting state - cut off the instant that state ends, even if the clip hasn't finished)")]
    [SerializeField] private AudioSource _interruptibleSfxSource;
    [Tooltip("Plays only for the active player, the moment it becomes their turn to input a word. Cut off the instant their turn ends.")]
    [SerializeField] private AudioClip _yourTurnClip;
    [Tooltip("Plays only for audience members (not the active player), a set number of seconds before voting closes. Cut off the instant voting closes.")]
    [SerializeField] private AudioClip _votingWarningClip;
    [SerializeField] private float _votingWarningSecondsBeforeEnd = 10f;

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
            PlayInterruptibleSfx(_votingWarningClip);
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
       
        StopInterruptibleSfx();

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
        
        bool isDuplicate = !float.IsNaN(_lastYourTurnGoalMin) &&
                           Mathf.Approximately(_lastYourTurnGoalMin, min) &&
                           Mathf.Approximately(_lastYourTurnGoalMax, max);

        if (isDuplicate)
            return;

        _lastYourTurnGoalMin = min;
        _lastYourTurnGoalMax = max;
        PlayInterruptibleSfx(_yourTurnClip);
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

    /// <summary>Plays on a dedicated source (not PlayOneShot) specifically so StopInterruptibleSfx can cut it off later(?).</summary>
    private void PlayInterruptibleSfx(AudioClip clip)
    {
        if (!_interruptibleSfxSource || !clip)
            return;

        _interruptibleSfxSource.clip = clip;
        _interruptibleSfxSource.Play();
    }

    private void StopInterruptibleSfx()
    {
        if (_interruptibleSfxSource && _interruptibleSfxSource.isPlaying)
            _interruptibleSfxSource.Stop();
    }
}
