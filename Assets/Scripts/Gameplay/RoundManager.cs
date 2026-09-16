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
/// scores each turn. Lives as a scene object in MainGame.unity (PurrNet
/// auto-spawns NetworkBehaviours already placed in a scene - no prefab or
/// runtime Instantiate needed).
///
/// Hidden information (the active player's secret goal, each vote before
/// reveal, everyone's score) never becomes a SyncVar - it lives only in
/// plain server-only fields below and is pushed to the right client via
/// TargetRpc. That's the entire privacy mechanism; nothing else is needed.
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

    [Header("Turn timers (seconds)")]
    [Tooltip("How long the active player has to submit a new label before voting opens anyway, unchanged.")]
    [SerializeField, Range(5f, 120f)] private float _targetingDuration = 30f;
    [Tooltip("How long the audience has to vote before the server force-reveals with whatever votes it has.")]
    [SerializeField, Range(5f, 180f)] private float _votingDuration = 45f;
    [Tooltip("Extra time the server waits past votingDuration before force-revealing, so a client's own time-triggered auto-submit has a chance to arrive over the network first.")]
    [SerializeField, Range(0f, 15f)] private float _votingTimeoutGraceSeconds = 3f;

    /// <summary>Same duration the server enforces - UI countdowns should read this instead of hardcoding a copy.</summary>
    public float targetingDuration => _targetingDuration;
    public float votingDuration => _votingDuration;

    // Public, server-authoritative, synced to every observer by default -
    // safe to be public because none of this is secret.
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

    // Server-only. Never synced, never broadcast except through the
    // explicit RPCs below - this is what keeps everything else hidden.
    private readonly List<PlayerID> _unpickedActivePlayers = new List<PlayerID>();
    private (float min, float max)? _currentGoalRange;
    private readonly Dictionary<PlayerID, float> _pendingVotes = new Dictionary<PlayerID, float>();
    private readonly HashSet<PlayerID> _votedThisTurn = new HashSet<PlayerID>();
    private readonly Dictionary<PlayerID, int> _scores = new Dictionary<PlayerID, int>();

    // Doubles as the server's resolved-name cache and each client's local
    // display-name cache (populated via the RPCs below) - display names
    // aren't secret, so one field serving both roles is fine.
    private readonly Dictionary<PlayerID, string> _displayNames = new Dictionary<PlayerID, string>();

    private Coroutine _phaseTimeoutRoutine;

    private readonly List<RoundContent> _roundsToPlay = new List<RoundContent>();
    private int _currentRoundIndex = -1;

    // Local-only mirrors: only ever populated on the one client an RPC
    // actually targeted. UI binds to these events, never to another
    // player's data.
    public (float min, float max)? localSecretGoal { get; private set; }
    public int localScore { get; private set; }

    public event Action<float, float> onLocalGoalReceived;
    public event Action<int, int> onLocalScoreChanged;
    public event Action<PlayerID, float> onVoteRevealed;
    public event Action<PlayerID, int> onFinalScoreRevealed;
    public event Action<PlayerID, string> onDisplayNameChanged;
    public event Action onVotingEnded;

    /// <summary>The player's PurrLobby display name if known yet, otherwise a fallback like "007".</summary>
    public string GetDisplayName(PlayerID player)
    {
        return _displayNames.TryGetValue(player, out var name) ? name : player.ToString();
    }

    public static bool TryGetLocalPlayerId(out PlayerID id)
    {
        // NetworkManager.playerModule prefers the server's PlayersManager
        // whenever one exists, which is always null on a listen-server host
        // (the server's own module never receives a login response - only
        // its client-role module does). Ask for the client-role module
        // explicitly so this also resolves correctly for the host.
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
            return;

        networkManager.onPlayerLoadedScene += OnPlayerLoadedScene;
        networkManager.onPlayerUnloadedScene += OnPlayerUnloadedScene;

        TryBeginRoundIfReady();
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

        // A player who finishes loading after the round already started still
        // needs a turn later, and still needs to count as audience - without
        // this they'd be silently skipped forever.
        if (phase.value != RoundPhase.WaitingForPlayers &&
            activePlayerId.value != player &&
            !_unpickedActivePlayers.Contains(player))
        {
            _unpickedActivePlayers.Add(player);
        }

        ResolveAndBroadcastDisplayName(player);
        TryBeginRoundIfReady();
    }

    /// <summary>
    /// Server-only. Bridges PurrNet's low-level PlayerID to PurrLobby's
    /// display name: PlayerID -> Connection (PlayersManager) -> lobby's own
    /// string player-id (IProvideConnectionToPlayerID) -> IPlayer.displayName
    /// (the active lobby). Same chain PurrLobbyPlayer.Setup already uses
    /// internally - this just makes the result available to everyone in the
    /// game scene instead of only the lobby-scene UI.
    /// </summary>
    private void ResolveAndBroadcastDisplayName(PlayerID player)
    {
        // Catch this (possibly late-joining) player up on every name already
        // known - a plain ObserversRpc broadcast at resolve-time would never
        // reach someone who joins after it fired.
        foreach (var kvp in _displayNames)
            Target_SetDisplayName(player, kvp.Key, kvp.Value);

        if (_displayNames.ContainsKey(player))
            return;

        if (!TryResolveDisplayName(player, out var name))
            return;

        _displayNames[player] = name;
        Rpc_SetDisplayName(player, name);
    }

    private bool TryResolveDisplayName(PlayerID player, out string displayName)
    {
        displayName = null;

        if (!networkManager.TryGetModule<PlayersManager>(true, out var players) ||
            !players.TryGetConnection(player, out var conn))
            return false;

        if (networkManager.authenticator is not IProvideConnectionToPlayerID provider ||
            !provider.TryGetPlayerID(conn, out var lobbyPlayerId))
            return false;

        var lobby = GameOrchestrator.active != null ? GameOrchestrator.active.activeLobby : null;
        if (lobby == null || !lobby.TryGetPlayer(lobbyPlayerId, out var lobbyPlayer))
            return false;

        displayName = lobbyPlayer.displayName;
        return true;
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

        StartNextRound(roster);
    }

    /// <summary>Randomly picks (without repeats, where possible) which authored Round Content entries this game will play, in order.</summary>
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

    /// <summary>Applies the next authored round's content and resets the active-player pool for it.</summary>
    private void StartNextRound(IReadOnlyList<PlayerID> roster)
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
        BeginTargeting();
    }

    private void BeginTargeting()
    {
        if (_unpickedActivePlayers.Count == 0)
        {
            bool hasMoreRounds = _currentRoundIndex + 1 < _roundsToPlay.Count;
            if (hasMoreRounds && networkManager.TryGetModule<PlayersManager>(true, out var players))
            {
                StartNextRound(players.players);
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

    /// <summary>The active player ran out of time - proceed to voting with whatever the labels already were, unchanged.</summary>
    private void OnTargetingTimeout()
    {
        if (phase.value != RoundPhase.Targeting)
            return;

        OpenVoting();
    }

    [TargetRpc]
    private void Target_ReceiveSecretGoal(PlayerID target, float goalMin, float goalMax)
    {
        localSecretGoal = (goalMin, goalMax);
        onLocalGoalReceived?.Invoke(goalMin, goalMax);
    }

    /// <summary>
    /// Lets the active player's own UI explicitly pull its secret goal
    /// instead of only relying on the one-shot push from BeginTargeting -
    /// that push can race a client whose own scene load/UI subscription is
    /// still settling (most likely right at game start). Safe to call any
    /// number of times; only re-sends to whoever actually is active.
    /// </summary>
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

        OpenVoting();
    }

    private void OpenVoting()
    {
        CancelPhaseTimeout();

        _pendingVotes.Clear();
        _votedThisTurn.Clear();
        votesSubmittedCount.value = 0;

        // Recompute from the live roster rather than trusting the snapshot
        // taken at round start - a straggler who joined a moment late must
        // still be counted as part of the audience.
        if (networkManager.TryGetModule<PlayersManager>(true, out var players))
            totalPlayers.value = players.players.Count;

        votesExpectedCount.value = Mathf.Max(0, totalPlayers.value - 1);
        phase.value = RoundPhase.VotingOpen;

        if (votesExpectedCount.value <= 0)
        {
            Reveal();
            return;
        }

        // Client-side auto-submit (using each straggler's own current slider
        // value) is expected to land first - this is only a backstop for a
        // client that never responds at all (disconnect, etc.), so it waits
        // a bit longer than the duration UI countdowns show.
        StartPhaseTimeout(_votingDuration + _votingTimeoutGraceSeconds, OnVotingTimeout);
    }

    /// <summary>
    /// Backstop only - reveals with whatever votes actually arrived. Clients
    /// are expected to auto-submit their own current slider value the moment
    /// their local countdown hits zero, so this should rarely fire with
    /// anyone still missing.
    /// </summary>
    private void OnVotingTimeout()
    {
        if (phase.value != RoundPhase.VotingOpen)
            return;

        Reveal();
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
            Reveal();
    }

    private void Reveal()
    {
        CancelPhaseTimeout();
        phase.value = RoundPhase.Revealing;

        // Explicit broadcast rather than relying on the phase SyncVar itself:
        // Revealing/Scoring/TurnComplete all happen synchronously in this
        // same call stack, so a client could observe phase jump straight
        // from VotingOpen to the next Targeting without ever seeing the
        // intermediate values - this RPC is still delivered as its own
        // discrete message regardless.
        Rpc_VotingEnded();

        float sum = 0f;
        foreach (var kvp in _pendingVotes)
        {
            sum += kvp.Value;
            Rpc_RevealOneVote(kvp.Key, kvp.Value);
        }

        pendulumValue.value = _pendingVotes.Count > 0 ? sum / _pendingVotes.Count : 0.5f;

        ScoreTurn();
    }

    [ObserversRpc]
    private void Rpc_RevealOneVote(PlayerID voterId, float position)
    {
        onVoteRevealed?.Invoke(voterId, position);
    }

    [ObserversRpc]
    private void Rpc_VotingEnded() => onVotingEnded?.Invoke();

    private void ScoreTurn()
    {
        phase.value = RoundPhase.Scoring;

        // Active player scores based on how close the pendulum's final
        // position landed to their secret goal range. Each audience member
        // separately scores based on how close their own vote landed to
        // that same (still-secret) goal range.
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

        phase.value = RoundPhase.GameOver;
    }

    [ObserversRpc]
    private void Rpc_RevealOneFinalScore(PlayerID playerId, int finalScore)
    {
        onFinalScoreRevealed?.Invoke(playerId, finalScore);
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
