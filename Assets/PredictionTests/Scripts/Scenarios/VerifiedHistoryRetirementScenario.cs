using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet.Prediction;
using UnityEngine;

public sealed class VerifiedHistoryRetirementScenario : Scenario
{
    private const int MeasuredCycleCount = 4;
    private const int TotalCycleCount = MeasuredCycleCount + 1;
    private const int StableSteps = 80;
    private const float TimeoutSeconds = 30f;
    private const int BarrierChannelBase = 940;
    private const long MaterialDeltaBytes = 256L * 1024L;
    private const long CumulativeRetentionBytes = 512L * 1024L;

    private GameObject _prefab;
    private int _prefabId;

    public override void Setup(ScenarioContext ctx, PurrNet.NetworkManager manager)
    {
        _prefab = PredictionTestUtils.CreatePrefab<HistoryStressIdentity>("VerifiedHistoryRetentionIdentity");
        var identity = _prefab.GetComponent<HistoryStressIdentity>();
        identity.payloadLength = 32;
        identity.listPayloadLength = 8;
        identity.targetSteps = StableSteps;
        PredictionTestUtils.RegisterPrefab(ctx, _prefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var manager = ctx.predictionManager;
        if (!manager.TryGetPrefab(_prefab, out _prefabId))
            return ScenarioResult.Fail("failed to resolve verified-history retention prefab id");

        await WaitForTicks(manager, manager.localTick + (ulong)Math.Max(4, manager.tickRate * 2), ctx);
        var initial = manager.CaptureVerifiedHistoryDiagnostics();
        var baseline = initial;
        long baselineMemory = 0;
        var storeCounts = new List<int>(MeasuredCycleCount);
        var entryCounts = new List<long>(MeasuredCycleCount);
        var memoryDeltas = new List<long>(MeasuredCycleCount);
        var postHistoryBytes = new List<long>(MeasuredCycleCount);
        var inputPlayerCounts = new List<int>(MeasuredCycleCount);
        var clientFrameCounts = new List<int>(MeasuredCycleCount);

        for (var cycle = 0; cycle < TotalCycleCount; cycle++)
        {
            if (ctx.isServer && !manager.hierarchy.Create(
                    _prefab,
                    new Vector3(cycle * 2f, 0f, 0f),
                    Quaternion.identity).HasValue)
            {
                return ScenarioResult.Fail($"cycle {cycle} failed to create the predicted identity");
            }

            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => TryGetOnlyIdentity(manager, out var identity) &&
                          identity.currentState.step >= StableSteps,
                    TimeoutSeconds,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail($"cycle {cycle} did not reach {StableSteps} simulated steps");
            }

            if (!TryGetOnlyIdentity(manager, out var liveIdentity))
                return ScenarioResult.Fail($"cycle {cycle} could not resolve its live identity");
            var objectId = liveIdentity.id.objectId;

            var present = await DigestExchange.Compare(
                ctx,
                BarrierChannelBase + cycle * 3,
                $"present:{cycle}:{objectId.instanceId.value}",
                TimeoutSeconds);
            if (!present.success)
                return present;

            if (ctx.isServer)
                manager.hierarchy.Delete(objectId);

            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => CountIdentities(manager) == 0 && !objectId.TryGetComponent<HistoryStressIdentity>(manager, out _),
                    TimeoutSeconds,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail($"cycle {cycle} did not remove object {objectId.instanceId.value}");
            }

            var removed = await DigestExchange.Compare(
                ctx,
                BarrierChannelBase + cycle * 3 + 1,
                $"removed:{cycle}",
                TimeoutSeconds);
            if (!removed.success)
                return removed;

            ulong horizonTick = manager.localTick + manager.verifiedHistoryRetentionTicks + 2;
            await WaitForTicks(manager, horizonTick, ctx);

            var horizon = await DigestExchange.Compare(
                ctx,
                BarrierChannelBase + cycle * 3 + 2,
                $"horizon:{cycle}",
                TimeoutSeconds);
            if (!horizon.success)
                return horizon;

            var diagnostics = manager.CaptureVerifiedHistoryDiagnostics();
            var retention = manager.CaptureRetentionSnapshot();
            var componentId = new PredictedComponentID(objectId, 0);
            if (manager.CountVerifiedStores(componentId) != 0)
            {
                return ScenarioResult.Fail(
                    $"cycle {cycle} retained verified stores for removed component {componentId}");
            }

            if (cycle == 0)
            {
                // The warm-up forces lazy built-in histories to their steady high-water mark.
                // Its removed identity must itself be retired before this baseline is accepted.
                baseline = diagnostics;
                baselineMemory = await CaptureMedianManagedBytes(ctx);
                continue;
            }

            storeCounts.Add(diagnostics.storeCount);
            entryCounts.Add(diagnostics.storedEntryCount);
            long managedBytes = await CaptureMedianManagedBytes(ctx);
            postHistoryBytes.Add(managedBytes);
            memoryDeltas.Add(managedBytes - baselineMemory);
            inputPlayerCounts.Add(retention.serverInputPlayerCount);
            clientFrameCounts.Add(retention.serverClientFrameCount);
        }

        var finalDiagnostics = manager.CaptureVerifiedHistoryDiagnostics();
        string memoryClassification = ClassifyRetention(postHistoryBytes, out var consecutiveDeltas);
        string evidence =
            $"initialStores={initial.storeCount}; baselineStores={baseline.storeCount}; " +
            $"stores={string.Join(",", storeCounts)}; " +
            $"entries={string.Join(",", entryCounts)}; memoryDeltas={string.Join(",", memoryDeltas)}; " +
            $"postHistoryBytes={string.Join(",", postHistoryBytes)}; " +
            $"consecutiveDeltas={string.Join(",", consecutiveDeltas)}; " +
            $"classification={memoryClassification}; inputPlayers={string.Join(",", inputPlayerCounts)}; " +
            $"clientFrames={string.Join(",", clientFrameCounts)}; " +
            $"scheduled={finalDiagnostics.retirementsScheduled}; " +
            $"cancelled={finalDiagnostics.retirementsCancelled}; " +
            $"completed={finalDiagnostics.retirementsCompleted}; " +
            $"disposed={finalDiagnostics.storesDisposed}; " +
            $"firstRetired={(finalDiagnostics.hasFirstRetiredComponent ? finalDiagnostics.firstRetiredComponent.ToString() : "none")}";

        if (storeCounts.Count != MeasuredCycleCount ||
            storeCounts.Exists(count => count != baseline.storeCount) ||
            finalDiagnostics.retiredComponentCount != 0 ||
            finalDiagnostics.retirementsCompleted < TotalCycleCount ||
            memoryClassification != "BoundedPlateau")
        {
            return ScenarioResult.Fail("verified-history stores did not return to baseline: " + evidence);
        }

        return ScenarioResult.Ok(evidence);
    }

    private static string ClassifyRetention(
        IReadOnlyList<long> postHistoryBytes,
        out List<long> consecutiveDeltas)
    {
        consecutiveDeltas = new List<long>(Math.Max(0, postHistoryBytes.Count - 1));
        if (postHistoryBytes.Count != MeasuredCycleCount)
            return "Inconclusive";

        int materialPositiveDeltas = 0;
        bool materialNegativeDelta = false;
        for (var i = 1; i < postHistoryBytes.Count; i++)
        {
            long delta = postHistoryBytes[i] - postHistoryBytes[i - 1];
            consecutiveDeltas.Add(delta);
            if (delta > MaterialDeltaBytes)
                materialPositiveDeltas++;
            if (delta < -MaterialDeltaBytes)
                materialNegativeDelta = true;
        }

        long cumulative = postHistoryBytes[postHistoryBytes.Count - 1] - postHistoryBytes[0];
        long finalDelta = consecutiveDeltas[consecutiveDeltas.Count - 1];
        if (materialPositiveDeltas > 0 && materialNegativeDelta)
            return "Inconclusive";
        if (materialPositiveDeltas >= 2 && cumulative > CumulativeRetentionBytes)
            return "PerIncarnationRetention";
        if (materialPositiveDeltas <= 1 && Math.Abs(finalDelta) <= MaterialDeltaBytes)
            return "BoundedPlateau";
        return "Inconclusive";
    }

    private bool TryGetOnlyIdentity(PredictionManager manager, out HistoryStressIdentity identity)
    {
        identity = null;
        ref var state = ref manager.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId != _prefabId)
                continue;
            if (!details.instanceId.TryGetComponent(manager, out identity))
                return false;
            return true;
        }

        return false;
    }

    private int CountIdentities(PredictionManager manager)
    {
        return PredictionTestUtils.CountInstances(manager, _prefabId);
    }

    private static async UniTask WaitForTicks(
        PredictionManager manager,
        ulong targetTick,
        ScenarioContext ctx)
    {
        await UniTaskUtils.WaitWithTimeout(
            () => manager.localTick >= targetTick,
            TimeoutSeconds,
            ctx.cancellationToken);
    }

    private static async UniTask<long> CaptureMedianManagedBytes(ScenarioContext ctx)
    {
        long first = GC.GetTotalMemory(true);
        await UniTask.NextFrame(ctx.cancellationToken);
        long second = GC.GetTotalMemory(true);
        await UniTask.NextFrame(ctx.cancellationToken);
        long third = GC.GetTotalMemory(true);
        if (first > second)
            (first, second) = (second, first);
        if (second > third)
            (second, third) = (third, second);
        if (first > second)
            (first, second) = (second, first);
        return second;
    }
}
