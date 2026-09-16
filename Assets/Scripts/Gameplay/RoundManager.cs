using System;
using System.Collections;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Lobby;
using PurrNet.Modules;
using UnityEngine;

/// <summary>
/// Server-authoritative referee for a full game: rotates the active player,
/// hands out the secret goal range, collects votes, reveals results, and
/// scores each turn.
///
/// Hidden information (the active player's secret goal, each vote before
/// reveal, everyone's score) 
///
/// The turn loop is a chain of coroutines, each pausing at a named phase for
/// a configurable duration before advancing
/// </summary>
public class RoundManager : NetworkBehaviour
{
    public static RoundManager instance { get; private set; }

    [Header("Round Content")]
    [Tooltip("Pool of possible rounds. Each game randomly picks Number Of Rounds of these (no repeats, unless there aren't enough).")]
    [SerializeField] private RoundContent[] _rounds =
    {
        new RoundContent
        {
            prompt = "Place a fifth grader within this scale of intelligence",
            leftLabel = "Puppy",
            rightLabel = "Seventh grader"
        }
    };
    [Tooltip("How many rounds a game plays before ending. Clamped down if there aren't enough Round Content entries to avoid repeats.")]
    [SerializeField, Min(1)] private int _numberOfRounds = 3;

    [Header("Scoring (placeholder tuning - needs a real pass)")]
    [SerializeField] private int _maxPointsPerTurn = 100;
    [SerializeField] private float _minGoalWidth = 0.1f;
    [SerializeField] private float _maxGoalWidth = 0.2f;

    [Header("Turn timers (seconds) - wait for input up to this long")]
    [Tooltip("How long the active player has to submit a new label before voting opens anyway, unchanged.")]
    [SerializeField, Range(5f, 120f)] private float _targetingDuration = 30f;
    [Tooltip("How long the audience has to vote before the server force-reveals with whatever votes it has.")]
    [SerializeField, Range(5f, 180f)] private float _votingDuration = 45f;
    [Tooltip("Extra time the server waits past votingDuration before force-revealing, so a client's own time-triggered auto-submit has a chance to arrive over the network first.")]
    [SerializeField, Range(0f, 15f)] private float _votingTimeoutGraceSeconds = 3f;

    [Header("Phase buffers (seconds) - fixed pause between steps for animations/audio")]
    [Tooltip("Before the targeting loop begins - at game start, and again at the start of every new round.")]
    [SerializeField, Range(0f, 10f)] private float _roundIntroDuration = 2f;
    [Tooltip("After the active player's turn ends, before voting opens.")]
    [SerializeField, Range(0f, 10f)] private float _targetingCompleteDuration = 1f;
    [Tooltip("After voting closes, before the player icons start moving.")]
    [SerializeField, Range(0f, 10f)] private float _votingCompleteDuration = 1f;
    [Tooltip("While the player icons animate to their revealed positions.")]
    [SerializeField, Range(0f, 10f)] private float _revealMovingDuration = 2f;
    [Tooltip("After the pendulum cue plays, before the pendulum starts moving.")]
    [SerializeField, Range(0f, 10f)] private float _pendulumCueDuration = 1f;
    [Tooltip("While the pendulum animates to its final position, before points are awarded.")]
    [SerializeField, Range(0f, 10f)] private float _pendulumMovingDuration = 2f;
    [Tooltip("After points are awarded, before the next active player is chosen.")]
    [SerializeField, Range(0f, 10f)] private float _turnCompleteDuration = 1f;

  
    public float targetingDuration => _targetingDuration;
    public float votingDuration => _votingDuration;

    // Public, server-authoritative, synced to every player
    // safe to be public because all of this is seen by the players
    public SyncVar<RoundPhase> phase = new SyncVar<RoundPhase>(RoundPhase.WaitingForPlayers);
    public SyncVar<string> promptText = new SyncVar<string>("");
    public SyncVar<string> leftLabel = new SyncVar<string>("");
    public SyncVar<string> rightLabel = new SyncVar<string>("");
    public SyncVar<PlayerID?> activePlayerId = new SyncVar<PlayerID?>(null);
    public SyncVar<int> votesSubmittedCount = new SyncVar<int>(0);
    public SyncVar<int> votesExpectedCount = new SyncVar<int>(0);
    public SyncVar<float> pendulumValue = new SyncVar<float>(0.5f);
    public SyncVar<int> turnNumber = new SyncVar<int>(0);
    public SyncVar<int> totalPlayers = new SyncVar<int>(0);
    public SyncVar<int> roundNumber = new SyncVar<int>(0);
    public SyncVar<int> totalRounds = new SyncVar<int>(0);

    // Server-only. 

        private readonly List<PlayerID> _unpickedActivePlayers = new List<PlayerID>();
    private (float min, float max)? _currentGoalRange;
    private readonly Dictionary<PlayerID, float> _pendingVotes = new Dictionary<PlayerID, float>();
    private readonly HashSet<PlayerID> _votedThisTurn = new HashSet<PlayerID>();
    private readonly Dictionary<PlayerID, int> _scores = new Dictionary<PlayerID, int>();
    private float _pendingPendulumValue;

    
    private readonly Dictionary<PlayerID, string> _displayNames = new Dictionary<PlayerID, string>();

    private Coroutine _phaseTimeoutRoutine;
    private Coroutine _phaseSequenceRoutine;

    private readonly List<RoundContent> _roundsToPlay = new List<RoundContent>();
    private int _currentRoundIndex = -1;

    
    public (float min, float max)? localSecretGoal { get; private set; }
    public int localScore { get; private set; }

    public event Action<float, float> onLocalGoalReceived;
    public event Action<int, int> onLocalScoreChanged;
    public event Action<PlayerID, float> onVoteRevealed;
    public event Action<PlayerID, int> onFinalScoreRevealed;
    public event Action<PlayerID?, PlayerID?, PlayerID?> onPodiumRevealed;
    public event Action<PlayerID, string> onDisplayNameChanged;
    public event Action onVotingEnded;

    /// <summary>The player's PurrLobby display name if it is found, otherwise a fallback like "001-002-ect".</summary>
    public string GetDisplayName(PlayerID player)
    {
        return _displayNames.TryGetValue(player, out var name) ? name : player.ToString();
    }

    public static bool TryGetLocalPlayerId(out PlayerID id)
    {

        var nm = NetworkManager.main;
        if (nm != null && nm.TryGetModule<PlayersManager>(false, out var clientPlayers) &&
            clientPlayers.localPlayerId.HasValue)
        {
            id = clientPlayers.localPlayerId.Value;
            return true;
        }

        id = default;
        return false;
    }

    protected override void OnSpawned(bool asServer)
    {
        base.OnSpawned(asServer);
        instance = this;

        if (!asServer)
        {
            ReportLocalDisplayName();
            return;
        }

        networkManager.onPlayerLoadedScene += OnPlayerLoadedScene;
        networkManager.onPlayerUnloadedScene += OnPlayerUnloadedScene;

        TryBeginRoundIfReady();
    }

    /// <summary>
    /// Client-only
    /// </summary>
    private void ReportLocalDisplayName()
    {
        var lobby = GameOrchestrator.active != null ? GameOrchestrator.active.activeLobby : null;
        var localPlayer = lobby != null ? lobby.localPlayer : null;

        if (localPlayer != null && !string.IsNullOrEmpty(localPlayer.displayName))
            Rpc_ReportMyDisplayName(localPlayer.displayName);
    }

    protected override void OnDestroy()
    {
        if (instance == this)
            instance = null;

        var nm = NetworkManager.main;
        if (nm != null)
        {
            nm.onPlayerLoadedScene -= OnPlayerLoadedScene;
            nm.onPlayerUnloadedScene -= OnPlayerUnloadedScene;
        }

        base.OnDestroy();
    }

    private bool IsThisScene(SceneID scene)
    {
        return networkManager.sceneModule.TryGetSceneID(gameObject.scene, out var sceneID) && sceneID == scene;
    }

    private void OnPlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
    {
        if (!asServer || !IsThisScene(scene))
            return;

        if (!_scores.ContainsKey(player))
            _scores[player] = 0;

    
        if (phase.value != RoundPhase.WaitingForPlayers &&
            activePlayerId.value != player &&
            !_unpickedActivePlayers.Contains(player))
        {
            _unpickedActivePlayers.Add(player);
        }

        TryBeginRoundIfReady();
    }

   
    [ServerRpc(requireOwnership: false)]
    private void Rpc_ReportMyDisplayName(string displayName, RPCInfo info = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return;

        
        foreach (var kvp in _displayNames)
            Target_SetDisplayName(info.sender, kvp.Key, kvp.Value);

        if (_displayNames.TryGetValue(info.sender, out var existing) && existing == displayName)
            return;

        _displayNames[info.sender] = displayName;
        Rpc_SetDisplayName(info.sender, displayName);
    }

    [ObserversRpc]
    private void Rpc_SetDisplayName(PlayerID player, string displayName) => ApplyDisplayName(player, displayName);

    [TargetRpc]
    private void Target_SetDisplayName(PlayerID target, PlayerID player, string displayName) =>
        ApplyDisplayName(player, displayName);

    private void ApplyDisplayName(PlayerID player, string displayName)
    {
        _displayNames[player] = displayName;
        onDisplayNameChanged?.Invoke(player, displayName);
    }

    private void OnPlayerUnloadedScene(PlayerID player, SceneID scene, bool asServer)
    {
        if (!asServer)
            return;

        _unpickedActivePlayers.Remove(player);
        _pendingVotes.Remove(player);
        _votedThisTurn.Remove(player);
    }

    private void TryBeginRoundIfReady()
    {
        if (!isServer || phase.value != RoundPhase.WaitingForPlayers)
            return;

        if (!networkManager.TryGetModule<PlayersManager>(true, out var players) || players.players.Count < 2)
            return;

        BeginRound(players.players);
    }

    /// <summary>Called once, when the game first has enough players. Sets up the whole game's round pool, then starts round 1.</summary>
    private void BeginRound(IReadOnlyList<PlayerID> roster)
    {
        foreach (var p in roster)
        {
            if (!_scores.ContainsKey(p))
                _scores[p] = 0;
        }

        PrepareRoundsToPlay();
        _currentRoundIndex = -1;

        BeginRoundIntroSequence(roster);
    }

    /// <summary>Randomly picks (without repeats, where possible) which a Round Content entries this game will play, in order.</summary>
    private void PrepareRoundsToPlay()
    {
        _roundsToPlay.Clear();

        if (_rounds == null || _rounds.Length == 0)
        {
            Debug.LogError("RoundManager has no Round Content configured.", this);
            return;
        }

        var pool = new List<RoundContent>(_rounds);
        Shuffle(pool);

        int count = Mathf.Min(_numberOfRounds, pool.Count);
        if (_numberOfRounds > pool.Count)
        {
            Debug.LogWarning($"RoundManager: requested {_numberOfRounds} rounds but only {pool.Count} " +
                              "Round Content entries are configured - playing all of them once instead.", this);
        }

        for (int i = 0; i < count; i++)
            _roundsToPlay.Add(pool[i]);

        totalRounds.value = _roundsToPlay.Count;
    }

    /// <summary>Applies the next authored round's content and resets the active-player pool, then pauses (RoundIntro) before the targeting loop begins.</summary>
    private void BeginRoundIntroSequence(IReadOnlyList<PlayerID> roster)
    {
        _currentRoundIndex++;
        roundNumber.value = _currentRoundIndex + 1; // 1-based for display

        var content = _roundsToPlay[_currentRoundIndex];
        promptText.value = content.prompt;
        leftLabel.value = content.leftLabel;
        rightLabel.value = content.rightLabel;

        _unpickedActivePlayers.Clear();
        _unpickedActivePlayers.AddRange(roster);
        Shuffle(_unpickedActivePlayers);

        totalPlayers.value = roster.Count;
        turnNumber.value = 0;

        phase.value = RoundPhase.RoundIntro;
        RunPhaseSequence(RoundIntroRoutine());
    }

    private IEnumerator RoundIntroRoutine()
    {
        yield return new WaitForSeconds(_roundIntroDuration);
        BeginTargeting();
    }

    private void BeginTargeting()
    {
        if (_unpickedActivePlayers.Count == 0)
        {
            bool hasMoreRounds = _currentRoundIndex + 1 < _roundsToPlay.Count;
            if (hasMoreRounds && networkManager.TryGetModule<PlayersManager>(true, out var players))
            {
                BeginRoundIntroSequence(players.players);
                return;
            }

            EndGame();
            return;
        }

        var next = _unpickedActivePlayers[_unpickedActivePlayers.Count - 1];
        _unpickedActivePlayers.RemoveAt(_unpickedActivePlayers.Count - 1);

        turnNumber.value++;
        activePlayerId.value = next;

        float goalWidth = UnityEngine.Random.Range(_minGoalWidth, _maxGoalWidth);
        float goalMin = UnityEngine.Random.Range(0f, 1f - goalWidth);
        _currentGoalRange = (goalMin, goalMin + goalWidth);

        phase.value = RoundPhase.Targeting;

        Target_ReceiveSecretGoal(next, _currentGoalRange.Value.min, _currentGoalRange.Value.max);

        StartPhaseTimeout(_targetingDuration, OnTargetingTimeout);
    }

    /// <summary>The active player ran out of time, so proceed with whatever the labels already were</summary>
    private void OnTargetingTimeout()
    {
        if (phase.value != RoundPhase.Targeting)
            return;

        BeginTargetingCompleteSequence();
    }

    [TargetRpc]
    private void Target_ReceiveSecretGoal(PlayerID target, float goalMin, float goalMax)
    {
        localSecretGoal = (goalMin, goalMax);
        onLocalGoalReceived?.Invoke(goalMin, goalMax);
    }

    
    [ServerRpc(requireOwnership: false)]
    public void Rpc_RequestSecretGoal(RPCInfo info = default)
    {
        if (phase.value != RoundPhase.Targeting)
            return;

        if (!activePlayerId.value.HasValue || activePlayerId.value.Value != info.sender)
            return;

        if (!_currentGoalRange.HasValue)
            return;

        Target_ReceiveSecretGoal(info.sender, _currentGoalRange.Value.min, _currentGoalRange.Value.max);
    }

    [ServerRpc(requireOwnership: false)]
    public void Rpc_SubmitEndpointLabel(bool isLeft, string newLabel, RPCInfo info = default)
    {
        if (phase.value != RoundPhase.Targeting)
            return;

        if (!activePlayerId.value.HasValue || activePlayerId.value.Value != info.sender)
            return;

        if (string.IsNullOrWhiteSpace(newLabel))
            return;

        if (isLeft)
            leftLabel.value = newLabel;
        else
            rightLabel.value = newLabel;

        BeginTargetingCompleteSequence();
    }

    private void BeginTargetingCompleteSequence()
    {
        CancelPhaseTimeout();
        phase.value = RoundPhase.TargetingComplete;
        RunPhaseSequence(TargetingCompleteRoutine());
    }

    private IEnumerator TargetingCompleteRoutine()
    {
        yield return new WaitForSeconds(_targetingCompleteDuration);
        OpenVoting();
    }

    private void OpenVoting()
    {
        _pendingVotes.Clear();
        _votedThisTurn.Clear();
        votesSubmittedCount.value = 0;

       
        // a straggler who joined a moment late must
        // still be counted as part of the audience.
        if (networkManager.TryGetModule<PlayersManager>(true, out var players))
            totalPlayers.value = players.players.Count;

        votesExpectedCount.value = Mathf.Max(0, totalPlayers.value - 1);
        phase.value = RoundPhase.VotingOpen;

        if (votesExpectedCount.value <= 0)
        {
            BeginVotingCompleteSequence();
            return;
        }

        
        StartPhaseTimeout(_votingDuration + _votingTimeoutGraceSeconds, OnVotingTimeout);
    }

   
    private void OnVotingTimeout()
    {
        if (phase.value != RoundPhase.VotingOpen)
            return;

        BeginVotingCompleteSequence();
    }

    [ServerRpc(requireOwnership: false)]
    public void Rpc_SubmitVote(float normalizedPosition, RPCInfo info = default)
    {
        if (phase.value != RoundPhase.VotingOpen)
            return;

        if (activePlayerId.value.HasValue && activePlayerId.value.Value == info.sender)
            return;

        if (!_votedThisTurn.Add(info.sender))
            return;

        _pendingVotes[info.sender] = Mathf.Clamp01(normalizedPosition);
        votesSubmittedCount.value = _votedThisTurn.Count;

        if (votesSubmittedCount.value >= votesExpectedCount.value)
            BeginVotingCompleteSequence();
    }

    private void BeginVotingCompleteSequence()
    {
        CancelPhaseTimeout();
        phase.value = RoundPhase.VotingComplete;

        
        Rpc_VotingEnded();

        RunPhaseSequence(VotingCompleteRoutine());
    }

    private IEnumerator VotingCompleteRoutine()
    {
        yield return new WaitForSeconds(_votingCompleteDuration);
        RevealVotes();
    }

    [ObserversRpc]
    private void Rpc_VotingEnded() => onVotingEnded?.Invoke();

    private void RevealVotes()
    {
        phase.value = RoundPhase.RevealMoving;

        float sum = 0f;
        foreach (var kvp in _pendingVotes)
        {
            sum += kvp.Value;
            Rpc_RevealOneVote(kvp.Key, kvp.Value);
        }

        // Not applied to the pendulumValue SyncVar yet
        _pendingPendulumValue = _pendingVotes.Count > 0 ? sum / _pendingVotes.Count : 0.5f;

        RunPhaseSequence(RevealMovingRoutine());
    }

    [ObserversRpc]
    private void Rpc_RevealOneVote(PlayerID voterId, float position)
    {
        onVoteRevealed?.Invoke(voterId, position);
    }

    private IEnumerator RevealMovingRoutine()
    {
        yield return new WaitForSeconds(_revealMovingDuration);
        BeginPendulumCue();
    }

    private void BeginPendulumCue()
    {
        phase.value = RoundPhase.PendulumCue;
        RunPhaseSequence(PendulumCueRoutine());
    }

    private IEnumerator PendulumCueRoutine()
    {
        yield return new WaitForSeconds(_pendulumCueDuration);
        BeginPendulumMoving();
    }

    private void BeginPendulumMoving()
    {
        phase.value = RoundPhase.PendulumMoving;
        pendulumValue.value = _pendingPendulumValue;
        RunPhaseSequence(PendulumMovingRoutine());
    }

    private IEnumerator PendulumMovingRoutine()
    {
        yield return new WaitForSeconds(_pendulumMovingDuration);
        ScoreTurn();
    }

    private void ScoreTurn()
    {
        phase.value = RoundPhase.Scoring;

        // Active player scores based on how close the pendulum's final
        // position landed to their secret goal range. Each audience member
        // separately scores based on how close their own vote landed to
        // that same goal range.
        if (_currentGoalRange.HasValue && activePlayerId.value.HasValue)
        {
            var (min, max) = _currentGoalRange.Value;

            int activeDelta = ScoreFromDistance(DistanceToRange(pendulumValue.value, min, max));
            ApplyScore(activePlayerId.value.Value, activeDelta);

            foreach (var kvp in _pendingVotes)
            {
                int audienceDelta = ScoreFromDistance(DistanceToRange(kvp.Value, min, max));
                ApplyScore(kvp.Key, audienceDelta);
            }
        }

        _currentGoalRange = null;
        phase.value = RoundPhase.TurnComplete;
        RunPhaseSequence(TurnCompleteRoutine());
    }

    private IEnumerator TurnCompleteRoutine()
    {
        yield return new WaitForSeconds(_turnCompleteDuration);
        BeginTargeting();
    }

    private void ApplyScore(PlayerID player, int delta)
    {
        _scores.TryGetValue(player, out var current);
        int updated = current + delta;
        _scores[player] = updated;
        Target_ReceiveTurnScoreDelta(player, delta, updated);
    }

    [TargetRpc]
    private void Target_ReceiveTurnScoreDelta(PlayerID target, int delta, int newTotal)
    {
        localScore = newTotal;
        onLocalScoreChanged?.Invoke(delta, newTotal);
    }

    private void EndGame()
    {
        foreach (var kvp in _scores)
            Rpc_RevealOneFinalScore(kvp.Key, kvp.Value);

        BroadcastPodium();

        phase.value = RoundPhase.GameOver;
    }

    [ObserversRpc]
    private void Rpc_RevealOneFinalScore(PlayerID playerId, int finalScore)
    {
        onFinalScoreRevealed?.Invoke(playerId, finalScore);
    }

    /// <summary>Ranks everyone by final score, descending, and tells who's 1st/2nd/3rd (null if fewer than that many players).</summary>
    private void BroadcastPodium()
    {
        var ranked = new List<PlayerID>(_scores.Keys);
        ranked.Sort((a, b) => _scores[b].CompareTo(_scores[a]));

        PlayerID? first = ranked.Count > 0 ? ranked[0] : (PlayerID?)null;
        PlayerID? second = ranked.Count > 1 ? ranked[1] : (PlayerID?)null;
        PlayerID? third = ranked.Count > 2 ? ranked[2] : (PlayerID?)null;

        Rpc_RevealPodium(first, second, third);
    }

    [ObserversRpc]
    private void Rpc_RevealPodium(PlayerID? first, PlayerID? second, PlayerID? third)
    {
        onPodiumRevealed?.Invoke(first, second, third);
    }

    private static float DistanceToRange(float value, float min, float max)
    {
        if (value < min) return min - value;
        if (value > max) return value - max;
        return 0f;
    }

    private int ScoreFromDistance(float distance)
    {
        // Placeholder linear falloff - the GDD states the relationship
        // (closer = more points) but not a formula. Needs a tuning pass.
        float t = Mathf.Clamp01(1f - distance * 2f);
        return Mathf.RoundToInt(t * _maxPointsPerTurn);
    }

    /// <summary>Runs one of the fixed-duration phase-buffer coroutines above, cancelling any previous one (defensive - callers shouldn't overlap, but a stray old routine must never keep running into the wrong phase).</summary>
    private void RunPhaseSequence(IEnumerator routine)
    {
        if (_phaseSequenceRoutine != null)
            StopCoroutine(_phaseSequenceRoutine);

        _phaseSequenceRoutine = StartCoroutine(routine);
    }

    private void StartPhaseTimeout(float duration, Action onTimeout)
    {
        CancelPhaseTimeout();
        _phaseTimeoutRoutine = StartCoroutine(PhaseTimeoutRoutine(duration, onTimeout));
    }

    private void CancelPhaseTimeout()
    {
        if (_phaseTimeoutRoutine == null)
            return;

        StopCoroutine(_phaseTimeoutRoutine);
        _phaseTimeoutRoutine = null;
    }

    private IEnumerator PhaseTimeoutRoutine(float duration, Action onTimeout)
    {
        yield return new WaitForSeconds(duration);
        _phaseTimeoutRoutine = null;
        onTimeout?.Invoke();
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
