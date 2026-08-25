using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using PurrNet.Transports;
using UnityEngine;

public static class HostMigrationSignals
{
    public static ulong promotedId;
    public static ushort newPort;
    public static bool planReceived;
    public static int planAcks;
    public static bool goReceived;

    public static void Reset()
    {
        promotedId = 0;
        newPort = 0;
        planReceived = false;
        planAcks = 0;
        goReceived = false;
    }

    [ObserversRpc(runLocally: true)]
    public static void BroadcastPlan(ulong playerId, ushort port)
    {
        promotedId = playerId;
        newPort = port;
        planReceived = true;
    }

    [ServerRpc(requireOwnership: false)]
    public static void AckPlan()
    {
        planAcks++;
    }

    [ObserversRpc(runLocally: true)]
    public static void BroadcastGo()
    {
        goReceived = true;
    }
}

public class HostMigrationWorldScenario : Scenario
{
    private const string SessionId = "prediction-tests-migration";
    private const uint SessionEpoch = 2;
    private const int PrePlanBarrier = 450;
    private const int DigestChannel = 500;
    private const ushort PortOffset = 91;

    [SerializeField] private float _timeout = 120f;
    [SerializeField] private float _migrationTimeout = 60f;
    [SerializeField] private float _settleSeconds = 3f;

    private DeterministicTickCounter _counter;
    private DeterministicTimedSpawner _spawner;
    private GameObject _rigPrefab;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        HostMigrationSignals.Reset();

        _counter = FindFirstObjectByType<DeterministicTickCounter>();
        _spawner = FindFirstObjectByType<DeterministicTimedSpawner>();

        if (manager.networkRules)
        {
            JsonUtility.FromJsonOverwrite(
                "{\"_hostMigrationRules\":{\"enableHostMigration\":true,\"migrateAsHost\":true,\"sharePlayerCookiesWithPeers\":true}}",
                manager.networkRules);
        }

        var marker = PredictionTestUtils.CreatePrefab<PredictedMarker>("MigrationMarker");
        PredictionTestUtils.RegisterPrefab(ctx, marker);
        if (_spawner)
            _spawner.markerPrefab = marker;

        _rigPrefab = CreateRigPrefab();
        PredictionTestUtils.RegisterPrefab(ctx, _rigPrefab);
    }

    private static GameObject CreateRigPrefab()
    {
        var root = new GameObject("MigrationRig");
        root.SetActive(false);
        DontDestroyOnLoad(root);
        var spawner = root.AddComponent<PredictedIdentitySpawner>();

        var child = new GameObject("MigrationRigIdentity");
        child.transform.SetParent(root.transform, false);
        var identity = child.AddComponent<NetworkIdentity>();

        typeof(PredictedIdentitySpawner)
            .GetField("_identitiesToSpawn", BindingFlags.NonPublic | BindingFlags.Instance)
            !.SetValue(spawner, new[] { identity });

        return root;
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        if (ctx.role == NetworkRole.Host)
            return ScenarioResult.Fail("host mode is not supported; run with -role server");

        if (!_counter || !_spawner)
            return ScenarioResult.Fail("scene is missing DeterministicTickCounter or DeterministicTimedSpawner");

        return ctx.isServer ? await RunAsOldServer(ctx) : await RunAsClient(ctx);
    }

    private async UniTask<ScenarioResult> RunAsOldServer(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var manager = ctx.networkManager;

        if (!pm.hierarchy.Create(_rigPrefab, new Vector3(50f, 0f, 0f), Quaternion.identity).HasValue)
            return ScenarioResult.Fail("failed to create migration rig");

        pm.TryGetPrefab(_rigPrefab, out var rigPrefabId);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _spawner.spawnedCount >= _spawner.spawnsPerWave &&
                      PredictionTestUtils.CountInstances(pm, rigPrefabId) >= 1,
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"pre-migration world never settled: spawned={_spawner.spawnedCount}/{_spawner.spawnsPerWave} rigs={PredictionTestUtils.CountInstances(pm, rigPrefabId)}");
        }

        await ScenarioBarrier.Wait(ctx, PrePlanBarrier, _timeout);

        var promoted = PickPromoted(ctx);
        if (!promoted.HasValue)
            return ScenarioResult.Fail("no eligible client to promote");

        if (manager.transport is not UDPTransport udp)
            return ScenarioResult.Fail("scenario requires UDPTransport");

        HostMigrationSignals.BroadcastPlan(promoted.Value.id.value, (ushort)(udp.serverPort + PortOffset));

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => HostMigrationSignals.planAcks >= ctx.expectedConnections,
                _migrationTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"only {HostMigrationSignals.planAcks}/{ctx.expectedConnections} clients acked the migration plan");
        }

        HostMigrationSignals.BroadcastGo();

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => manager.playerCount == 0,
                _migrationTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"{manager.playerCount} clients never detached from the old server");
        }

        return ScenarioResult.Ok();
    }

    private async UniTask<ScenarioResult> RunAsClient(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var manager = ctx.networkManager;
        pm.TryGetPrefab(_rigPrefab, out var rigPrefabId);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _spawner.spawnedCount >= _spawner.spawnsPerWave &&
                      PredictionTestUtils.CountInstances(pm, rigPrefabId) >= 1 &&
                      FindRigIdentity() is { isSpawned: true },
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"pre-migration world never settled: spawned={_spawner.spawnedCount}/{_spawner.spawnsPerWave} rigs={PredictionTestUtils.CountInstances(pm, rigPrefabId)} rigIdentity={FindRigIdentity()?.isSpawned.ToString() ?? "missing"}");
        }

        await ScenarioBarrier.Wait(ctx, PrePlanBarrier, _timeout);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => HostMigrationSignals.planReceived,
                _migrationTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("migration plan broadcast never arrived");
        }

        var preCounter = _counter.currentState.count;
        var rigIdentity = FindRigIdentity();
        var preNetworkId = rigIdentity ? rigIdentity.id : null;
        if (!preNetworkId.HasValue)
            return ScenarioResult.Fail("rig identity was not spawned locally before migration");

        bool isPromoted = manager.isLocalPlayerReady &&
                          manager.localPlayer.id.value == HostMigrationSignals.promotedId;

        HostMigrationSignals.AckPlan();

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => HostMigrationSignals.goReceived,
                _migrationTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("migration go broadcast never arrived");
        }

        if (manager.transport is not UDPTransport udp)
            return ScenarioResult.Fail("scenario requires UDPTransport");

        udp.serverPort = HostMigrationSignals.newPort;

        var options = new HostMigrationTransitionOptions(
            SessionId, SessionEpoch, new List<PlayerID>(manager.players));

        HostMigrationTransitionResult result;
        if (isPromoted)
        {
            udp.maxConnections = Mathf.Max(udp.maxConnections, ctx.expectedConnections + 8);
            result = await manager.PromoteToServerAsync(options, _migrationTimeout);
        }
        else
        {
            await UniTask.WaitForSeconds(0.5f, cancellationToken: ctx.cancellationToken);
            result = await manager.TransferToNewServerAsync(options, _migrationTimeout);
        }

        if (!result.succeeded)
        {
            return ScenarioResult.Fail(
                $"{(isPromoted ? "promotion" : "transfer")} failed: {result.status} {result.message}");
        }

        var post = ctx;
        post.role = isPromoted ? NetworkRole.Host : NetworkRole.Client;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => manager.isClient && manager.isLocalPlayerReady && (!isPromoted || manager.isServer),
                _migrationTimeout,
                post.cancellationToken);

            if (isPromoted)
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => manager.playerCount >= ctx.expectedConnections && !manager.isHostMigrationRosterPending,
                    _migrationTimeout,
                    post.cancellationToken);
            }

            var tickAtResume = pm.localTick;
            await UniTaskUtils.WaitWithTimeout(
                () => pm.localTick > tickAtResume + 5,
                _migrationTimeout,
                post.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"post-migration session never resumed: server={manager.isServer} client={manager.isClient} ready={manager.isLocalPlayerReady} players={manager.playerCount}/{ctx.expectedConnections} rosterPending={manager.isHostMigrationRosterPending} tick={pm.localTick}");
        }

        if (!rigIdentity || !rigIdentity.isSpawned || rigIdentity.id != preNetworkId)
        {
            return ScenarioResult.Fail(
                $"rig identity did not survive migration: alive={(bool)rigIdentity} spawned={(rigIdentity ? rigIdentity.isSpawned : false)} id={(rigIdentity ? rigIdentity.id?.ToString() : "gone")} expected={preNetworkId}");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _counter.currentState.count >= preCounter,
                10f,
                post.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"deterministic counter regressed across migration: {_counter.currentState.count} < {preCounter}");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _spawner.spawnedCount >= _spawner.totalSpawns,
                _timeout,
                post.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"post-migration wave never completed: spawned={_spawner.spawnedCount}/{_spawner.totalSpawns}");
        }

        if (PredictionTestUtils.CountInstances(pm, rigPrefabId) != 1)
            return ScenarioResult.Fail(
                $"rig instance count diverged after migration: {PredictionTestUtils.CountInstances(pm, rigPrefabId)}");

        await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: post.cancellationToken);
        await PredictionTestUtils.AlignDigestTick(post, DigestChannel, _timeout);

        var digest = PredictionTestUtils.WorldDigest(post, _counter);
        var compare = await DigestExchange.Compare(post, DigestChannel, digest, 30f);

        if (isPromoted)
            ScenarioSequencer.IssueSequenceComplete();

        return compare;
    }

    private static NetworkIdentity FindRigIdentity()
    {
        var spawners = FindObjectsByType<PredictedIdentitySpawner>(FindObjectsSortMode.None);
        for (int i = 0; i < spawners.Length; i++)
        {
            var identity = spawners[i].GetComponentInChildren<NetworkIdentity>(true);
            if (identity)
                return identity;
        }

        return null;
    }

    private static PlayerID? PickPromoted(ScenarioContext ctx)
    {
        PlayerID? best = null;
        var players = ctx.networkManager.players;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p.isServer)
                continue;
            if (!best.HasValue || p.id.value < best.Value.id.value)
                best = p;
        }

        return best;
    }
}
