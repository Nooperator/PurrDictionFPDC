using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class VerifiedHistoryRetirementTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void LiveUnregistrationRetainsStoresThroughHorizonThenDisposesEverySubkey()
        {
            var managerObject = new GameObject("Verified history manager");
            var identityObject = new GameObject("Verified identity");
            try
            {
                var manager = CreateManager(managerObject, 40);
                var id = new PredictedComponentID(new PredictedObjectID(1001), 0);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                identity.AttachForTest(manager, id);
                Register(manager, identity);

                var firstCounter = new DisposalCounter();
                var secondCounter = new DisposalCounter();
                manager.GetVerifiedHistory<TrackedState>(id, out _)
                    .Write(1, new TrackedState(firstCounter));
                manager.GetVerifiedHistory<TrackedState>(id, 3, out _)
                    .Write(1, new TrackedState(secondCounter));
                manager.GetVerifiedHistory<OmittedState>(id, 7, out _)
                    .Write(1, new OmittedState { value = 7 });

                Assert.That(manager.CaptureVerifiedHistoryDiagnostics().storeCount, Is.EqualTo(3));
                manager.UnregisterInstance(identity);

                var retired = manager.CaptureVerifiedHistoryDiagnostics();
                Assert.That(retired.retiredComponentCount, Is.EqualTo(1));
                Assert.That(retired.retirementsScheduled, Is.EqualTo(1));
                Assert.That(retired.hasFirstRetiredComponent, Is.True);
                Assert.That(retired.firstRetiredComponent, Is.EqualTo(id));

                manager.PruneRetiredVerifiedStores(0);
                Assert.That(manager.CaptureVerifiedHistoryDiagnostics().storeCount, Is.EqualTo(3),
                    "a backwards client tick must not expire a future retirement boundary");

                manager.PruneRetiredVerifiedStores(401);
                Assert.That(manager.CaptureVerifiedHistoryDiagnostics().storeCount, Is.EqualTo(3));
                Assert.That(firstCounter.disposals, Is.Zero);
                Assert.That(secondCounter.disposals, Is.Zero);

                manager.PruneRetiredVerifiedStores(402);
                var pruned = manager.CaptureVerifiedHistoryDiagnostics();
                Assert.That(pruned.storeCount, Is.Zero);
                Assert.That(pruned.retiredComponentCount, Is.Zero);
                Assert.That(manager.CountVerifiedStores(id), Is.Zero);
                Assert.That(pruned.retirementsCompleted, Is.EqualTo(1));
                Assert.That(pruned.storesDisposed, Is.EqualTo(3));
                Assert.That(firstCounter.disposals, Is.EqualTo(1));
                Assert.That(secondCounter.disposals, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void SameIdHistoryReuseCancelsRetirement()
        {
            var managerObject = new GameObject("Verified history manager");
            var firstObject = new GameObject("First identity");
            var replacementObject = new GameObject("Replacement identity");
            try
            {
                var manager = CreateManager(managerObject, 40);
                var id = new PredictedComponentID(new PredictedObjectID(1002), 0);
                var first = firstObject.AddComponent<OmittedStateIdentity>();
                first.AttachForTest(manager, id);
                Register(manager, first);
                var history = manager.GetVerifiedHistory<OmittedState>(id, out _);
                history.Write(1, new OmittedState { value = 11 });

                manager.UnregisterInstance(first);
                Assert.That(manager.CaptureVerifiedHistoryDiagnostics().retiredComponentCount, Is.EqualTo(1));

                var replacement = replacementObject.AddComponent<OmittedStateIdentity>();
                replacement.AttachForTest(manager, id);
                Register(manager, replacement);
                var reused = manager.GetVerifiedHistory<OmittedState>(id, out var created);

                Assert.That(created, Is.False);
                Assert.That(reused, Is.SameAs(history));
                Assert.That(manager.CaptureVerifiedHistoryDiagnostics().retiredComponentCount, Is.Zero);

                manager.PruneRetiredVerifiedStores(1000);
                var active = manager.CaptureVerifiedHistoryDiagnostics();
                Assert.That(active.storeCount, Is.EqualTo(1));
                Assert.That(active.retirementsCancelled, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(replacementObject);
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void StalePooledUnregistrationCannotRetireLiveReplacementStores()
        {
            var managerObject = new GameObject("Verified history manager");
            var staleObject = new GameObject("Stale identity");
            var liveObject = new GameObject("Live identity");
            try
            {
                var manager = CreateManager(managerObject, 40);
                var id = new PredictedComponentID(new PredictedObjectID(1003), 0);
                var stale = staleObject.AddComponent<OmittedStateIdentity>();
                stale.AttachForTest(manager, id);
                Register(manager, stale);
                manager.GetVerifiedHistory<OmittedState>(id, out _)
                    .Write(1, new OmittedState { value = 13 });

                var live = liveObject.AddComponent<OmittedStateIdentity>();
                live.AttachForTest(manager, id);
                Register(manager, live);
                manager.UnregisterInstance(stale);

                var diagnostics = manager.CaptureVerifiedHistoryDiagnostics();
                Assert.That(manager.GetIdentity(id), Is.SameAs(live));
                Assert.That(diagnostics.storeCount, Is.EqualTo(1));
                Assert.That(diagnostics.retiredComponentCount, Is.Zero);
                Assert.That(diagnostics.retirementsScheduled, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(liveObject);
                Object.DestroyImmediate(staleObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void FreshIdsRetireIndependently()
        {
            var managerObject = new GameObject("Verified history manager");
            var firstObject = new GameObject("First identity");
            var secondObject = new GameObject("Second identity");
            try
            {
                var manager = CreateManager(managerObject, 20);
                var firstId = new PredictedComponentID(new PredictedObjectID(1004), 0);
                var secondId = new PredictedComponentID(new PredictedObjectID(1005), 0);
                var first = firstObject.AddComponent<OmittedStateIdentity>();
                var second = secondObject.AddComponent<OmittedStateIdentity>();
                first.AttachForTest(manager, firstId);
                second.AttachForTest(manager, secondId);
                Register(manager, first);
                Register(manager, second);
                manager.GetVerifiedHistory<OmittedState>(firstId, out _)
                    .Write(1, new OmittedState { value = 1 });
                manager.GetVerifiedHistory<OmittedState>(secondId, out _)
                    .Write(1, new OmittedState { value = 2 });

                manager.UnregisterInstance(first);
                manager.PruneRetiredVerifiedStores(202);

                var diagnostics = manager.CaptureVerifiedHistoryDiagnostics();
                Assert.That(diagnostics.storeCount, Is.EqualTo(1));
                Assert.That(diagnostics.retirementsCompleted, Is.EqualTo(1));
                Assert.That(manager.GetVerifiedHistory<OmittedState>(secondId, out var created).Count, Is.EqualTo(1));
                Assert.That(created, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(secondObject);
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        private static PredictionManager CreateManager(GameObject managerObject, int tickRate)
        {
            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(manager, "<tickRate>k__BackingField", tickRate);
            return manager;
        }

        private static void Register(PredictionManager manager, PredictedIdentity identity)
        {
            var systems = GetField<List<PredictedIdentity>>(manager, "_systems");
            systems.Add(identity);
            SetField(manager, "_systemsCount", systems.Count);
            var instanceMap = GetField<Dictionary<PredictedComponentID, PredictedIdentity>>(manager, "_instanceMap");
            instanceMap[identity.id] = identity;
        }

        private static T GetField<T>(PredictionManager manager, string name)
        {
            var field = typeof(PredictionManager).GetField(name, InstanceFields);
            Assert.That(field, Is.Not.Null, $"missing field {name}");
            return (T)field.GetValue(manager);
        }

        private static void SetField(PredictionManager manager, string name, object value)
        {
            var field = typeof(PredictionManager).GetField(name, InstanceFields);
            Assert.That(field, Is.Not.Null, $"missing field {name}");
            field.SetValue(manager, value);
        }

        private sealed class DisposalCounter
        {
            public int disposals;
        }

        private readonly struct TrackedState : IDisposable
        {
            private readonly DisposalCounter _counter;

            public TrackedState(DisposalCounter counter)
            {
                _counter = counter;
            }

            public void Dispose()
            {
                if (_counter != null)
                    _counter.disposals++;
            }
        }
    }
}
