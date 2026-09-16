using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Modules;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PurrNet
{
    /// <summary>
    ///  replacement for PurrNet's own PlayerSpawner that rotates
    /// through a list of prefabs instead of always spawning the same one 
    /// </summary>
    [AddComponentMenu("PurrNet/Multi-Prefab Player Spawner")]
    public class MultiPrefabPlayerSpawner : PurrMonoBehaviour
    {
        [SerializeField] private GameObject[] _playerPrefabs;
        [Tooltip("Even if rules are to not despawn on disconnect, this will ignore that and always spawn a player.")]
        [SerializeField] private bool _ignoreNetworkRules;

        [SerializeField] private List<Transform> spawnPoints = new List<Transform>();
        public IReadOnlyList<Transform> SpawnPoints => spawnPoints;
        private int _currentSpawnPoint;

        private int _nextPrefabIndex;

        private IProvideSpawnPoints _spawnPointProvider;
        private IProvidePrefabInstantiated _prefabInstantiatedProvider;

        public void SetRespawnPointProvider(IProvideSpawnPoints provider) => _spawnPointProvider = provider;
        public void ResetSpawnPointProvider() => _spawnPointProvider = null;
        public void SetPrefabInstantiatedProvider(IProvidePrefabInstantiated provider) => _prefabInstantiatedProvider = provider;
        public void ResetPrefabInstantiatedProvider() => _prefabInstantiatedProvider = null;

        private void Awake()
        {
            CleanupSpawnPoints();
        }

        private void CleanupSpawnPoints()
        {
            bool hadNullEntry = false;
            for (int i = 0; i < spawnPoints.Count; i++)
            {
                if (!spawnPoints[i])
                {
                    hadNullEntry = true;
                    spawnPoints.RemoveAt(i);
                    i--;
                }
            }

            if (hadNullEntry)
                PurrLogger.LogWarning("Some spawn points were invalid and have been cleaned up.", this);
        }

        public override void Subscribe(NetworkManager manager, bool asServer)
        {
            if (asServer && manager.TryGetModule(out ScenePlayersModule scenePlayersModule, true))
            {
                scenePlayersModule.onPlayerLoadedScene += OnPlayerLoadedScene;

                if (!manager.TryGetModule(out ScenesModule scenes, true))
                    return;

                if (!scenes.TryGetSceneID(gameObject.scene, out var sceneID))
                    return;

                if (scenePlayersModule.TryGetPlayersInScene(sceneID, out var players))
                {
                    foreach (var player in players)
                        OnPlayerLoadedScene(player, sceneID, true);
                }
            }
        }

        public override void Unsubscribe(NetworkManager manager, bool asServer)
        {
            if (asServer && manager.TryGetModule(out ScenePlayersModule scenePlayersModule, true))
                scenePlayersModule.onPlayerLoadedScene -= OnPlayerLoadedScene;
        }

        private void OnDestroy()
        {
            if (NetworkManager.main &&
                NetworkManager.main.TryGetModule(out ScenePlayersModule scenePlayersModule, true))
                scenePlayersModule.onPlayerLoadedScene -= OnPlayerLoadedScene;
        }

        private void OnPlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            var main = NetworkManager.main;

            if (!main || !main.TryGetModule(out ScenesModule scenes, true))
                return;

            var unityScene = gameObject.scene;

            if (!scenes.TryGetSceneID(unityScene, out var sceneID))
                return;

            if (sceneID != scene)
                return;

            if (!asServer)
                return;

            if (_playerPrefabs == null || _playerPrefabs.Length == 0)
            {
                PurrLogger.LogError("MultiPrefabPlayerSpawner has no player prefabs configured.", this);
                return;
            }

            bool isDestroyOnDisconnectEnabled = main.networkRules.ShouldDespawnOnOwnerDisconnect();
            if (!_ignoreNetworkRules && !isDestroyOnDisconnectEnabled && PlayerOwnsAnyPlayerPrefab(main, player))
                return;

            var prefab = _playerPrefabs[_nextPrefabIndex % _playerPrefabs.Length];
            _nextPrefabIndex = (_nextPrefabIndex + 1) % _playerPrefabs.Length;

            GameObject newPlayer;
            NetworkIdentity identity;

            CleanupSpawnPoints();

            if (_spawnPointProvider != null)
            {
                var point = _spawnPointProvider.NextSpawnPoint(player, scene);
                newPlayer = SpawnPlayer(prefab, point.position, point.rotation, unityScene, out identity);
            }
            else if (spawnPoints.Count > 0)
            {
                var spawnPoint = spawnPoints[_currentSpawnPoint];
                _currentSpawnPoint = (_currentSpawnPoint + 1) % spawnPoints.Count;
                newPlayer = SpawnPlayer(prefab, spawnPoint.position, spawnPoint.rotation, unityScene, out identity);
            }
            else
            {
                prefab.transform.GetPositionAndRotation(out var position, out var rotation);
                newPlayer = SpawnPlayer(prefab, position, rotation, unityScene, out identity);
            }

            if (!newPlayer)
                return;

            _prefabInstantiatedProvider?.OnPrefabInstantiated(newPlayer, player, scene);

            if (identity)
                identity.GiveOwnership(player);
        }

        /// <summary>True if this player already owns a spawned instance of any prefab in the list (a reconnect, not a first join).</summary>
        private bool PlayerOwnsAnyPlayerPrefab(NetworkManager main, PlayerID player)
        {
            if (!main.TryGetModule(out GlobalOwnershipModule ownership, true))
                return false;

            if (main.prefabProvider == null)
                return false;

            foreach (var prefab in _playerPrefabs)
            {
                if (!prefab || !main.prefabProvider.TryGetPrefabData(prefab, out var prefabData))
                    continue;

                foreach (var owned in ownership.EnumerateAllPlayerOwnedIds(player))
                {
                    if (owned && owned.scopedPrefabId == prefabData.prefabId)
                        return true;
                }
            }

            return false;
        }

        private GameObject SpawnPlayer(GameObject prefab, Vector3 position, Quaternion rotation, Scene unityScene,
            out NetworkIdentity identity)
        {
            identity = null;

            var newPlayer = UnityProxy.Instantiate(prefab, position, rotation, unityScene);

            if (!newPlayer)
                return null;

            if (!newPlayer.TryGetComponent(out identity))
                return newPlayer;

            var nm = manager ? manager : NetworkManager.main;

            if (nm && !identity.IsSpawned(nm.isServer))
                nm.Spawn(newPlayer);

            return newPlayer;
        }
    }
}
