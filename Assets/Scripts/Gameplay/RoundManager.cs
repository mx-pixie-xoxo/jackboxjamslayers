using System;
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

    [Header("Round Content (placeholder)")]
    [SerializeField] private string _defaultPrompt = "Place a fifth grader within this scale of intelligence";
    [SerializeField] private string _defaultLeftLabel = "Puppy";
    [SerializeField] private string _defaultRightLabel = "Seventh grader";

    [Header("Scoring (placeholder tuning - needs a real pass)")]
    [SerializeField] private int _maxPointsPerTurn = 100;
    [SerializeField] private float _minGoalWidth = 0.1f;
    [SerializeField] private float _maxGoalWidth = 0.2f;

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

    private void BeginRound(IReadOnlyList<PlayerID> roster)
    {
        promptText.value = _defaultPrompt;
        leftLabel.value = _defaultLeftLabel;
        rightLabel.value = _defaultRightLabel;

        _unpickedActivePlayers.Clear();
        _unpickedActivePlayers.AddRange(roster);
        Shuffle(_unpickedActivePlayers);

        totalPlayers.value = roster.Count;
        turnNumber.value = 0;

        foreach (var p in roster)
        {
            if (!_scores.ContainsKey(p))
                _scores[p] = 0;
        }

        phase.value = RoundPhase.RoundIntro;
        BeginTargeting();
    }

    private void BeginTargeting()
    {
        if (_unpickedActivePlayers.Count == 0)
        {
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
        phase.value = RoundPhase.Revealing;

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

    private void ScoreTurn()
    {
        phase.value = RoundPhase.Scoring;

        // Only the active player scores, based on how close the pendulum's
        // final position landed to their secret goal range. Audience members
        // don't earn points for voting.
        if (_currentGoalRange.HasValue && activePlayerId.value.HasValue)
        {
            var (min, max) = _currentGoalRange.Value;
            int activeDelta = ScoreFromDistance(DistanceToRange(pendulumValue.value, min, max));
            ApplyScore(activePlayerId.value.Value, activeDelta);
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

    private static void Shuffle(IList<PlayerID> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
