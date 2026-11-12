using NUnit.Framework;
using UnityEngine;

namespace PICO.Samples.HandJointsOverlay.Tests
{
    public class HandJointHistoryTests
    {
        [Test]
        public void Sample_BetweenTrackedSnapshots_InterpolatesEachValidJoint()
        {
            var history = new TimedHandJointHistory(jointCount: 2, capacity: 4);
            history.Push(100d, Positions(0f, 10f), Valid(true, true), tracked: true);
            history.Push(200d, Positions(10f, 30f), Valid(true, true), tracked: true);
            var output = new Vector3[2];
            var valid = new bool[2];

            HandHistorySampleResult result = history.Sample(150d, output, valid);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.Success));
            Assert.That(output[0].x, Is.EqualTo(5f).Within(0.0001f));
            Assert.That(output[1].x, Is.EqualTo(20f).Within(0.0001f));
            Assert.That(valid, Is.EqualTo(new[] { true, true }));
        }

        [Test]
        public void Sample_ExactOldest_ReturnsSnapshot()
        {
            TimedHandJointHistory history = CreateTwoSnapshotHistory();
            var output = new Vector3[2];
            var valid = new bool[2];

            HandHistorySampleResult result = history.Sample(100d, output, valid);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.Success));
            Assert.That(output[0].x, Is.EqualTo(0f));
            Assert.That(output[1].x, Is.EqualTo(10f));
            Assert.That(valid, Is.EqualTo(new[] { true, true }));
        }

        [Test]
        public void Sample_ExactNewest_ReturnsSnapshot()
        {
            TimedHandJointHistory history = CreateTwoSnapshotHistory();
            var output = new Vector3[2];
            var valid = new bool[2];

            HandHistorySampleResult result = history.Sample(200d, output, valid);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.Success));
            Assert.That(output[0].x, Is.EqualTo(10f));
            Assert.That(output[1].x, Is.EqualTo(30f));
        }

        [Test]
        public void Sample_SingleSnapshot_AllowsExactHitOnly()
        {
            var history = new TimedHandJointHistory(jointCount: 1, capacity: 4);
            history.Push(100d, Positions(5f), Valid(true), tracked: true);
            var output = new Vector3[1];
            var valid = new bool[1];

            Assert.That(
                history.Sample(100d, output, valid),
                Is.EqualTo(HandHistorySampleResult.Success));
            Assert.That(output[0].x, Is.EqualTo(5f));
            Assert.That(
                history.Sample(99d, output, valid),
                Is.EqualTo(HandHistorySampleResult.TargetBeforeOldest));
            Assert.That(
                history.Sample(101d, output, valid),
                Is.EqualTo(HandHistorySampleResult.TargetAfterNewest));
        }

        [Test]
        public void Sample_BeforeOldest_ClearsValidity()
        {
            TimedHandJointHistory history = CreateTwoSnapshotHistory();
            var output = new Vector3[2];
            var valid = new[] { true, true };

            HandHistorySampleResult result = history.Sample(99d, output, valid);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.TargetBeforeOldest));
            Assert.That(valid, Is.EqualTo(new[] { false, false }));
        }

        [Test]
        public void Sample_AfterNewest_ClearsValidity()
        {
            TimedHandJointHistory history = CreateTwoSnapshotHistory();
            var output = new Vector3[2];
            var valid = new[] { true, true };

            HandHistorySampleResult result = history.Sample(201d, output, valid);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.TargetAfterNewest));
            Assert.That(valid, Is.EqualTo(new[] { false, false }));
        }

        [Test]
        public void Sample_TrackedGap_ReturnsUnavailableAndClearsValidity()
        {
            var history = new TimedHandJointHistory(jointCount: 1, capacity: 4);
            history.Push(100d, Positions(0f), Valid(true), tracked: true);
            history.Push(200d, Positions(10f), Valid(true), tracked: false);
            var output = new Vector3[1];
            var valid = new[] { true };

            HandHistorySampleResult result = history.Sample(150d, output, valid);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.TrackingUnavailable));
            Assert.That(valid[0], Is.False);
        }

        [Test]
        public void Sample_ExactUntrackedSnapshot_ReturnsUnavailable()
        {
            var history = new TimedHandJointHistory(jointCount: 1, capacity: 4);
            history.Push(100d, Positions(0f), Valid(true), tracked: false);
            var output = new Vector3[1];
            var valid = new[] { true };

            HandHistorySampleResult result = history.Sample(100d, output, valid);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.TrackingUnavailable));
            Assert.That(valid[0], Is.False);
        }

        [Test]
        public void Sample_InvalidJoint_RemainsInvalidWhileOtherJointInterpolates()
        {
            var history = new TimedHandJointHistory(jointCount: 2, capacity: 4);
            history.Push(100d, Positions(0f, 10f), Valid(true, false), tracked: true);
            history.Push(200d, Positions(10f, 30f), Valid(true, true), tracked: true);
            var output = new Vector3[2];
            var valid = new bool[2];

            HandHistorySampleResult result = history.Sample(150d, output, valid);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.Success));
            Assert.That(output[0].x, Is.EqualTo(5f).Within(0.0001f));
            Assert.That(valid[0], Is.True);
            Assert.That(valid[1], Is.False);
        }

        [Test]
        public void Push_DuplicateTimestamp_OverwritesNewestSnapshot()
        {
            var history = new TimedHandJointHistory(jointCount: 1, capacity: 4);
            history.Push(100d, Positions(1f), Valid(true), tracked: true);
            history.Push(100d, Positions(2f), Valid(true), tracked: true);
            var output = new Vector3[1];
            var valid = new bool[1];

            HandHistorySampleResult result = history.Sample(100d, output, valid);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.Success));
            Assert.That(output[0].x, Is.EqualTo(2f));
        }

        [Test]
        public void Push_TimestampRegression_ResetsHistory()
        {
            var history = new TimedHandJointHistory(jointCount: 1, capacity: 4);
            history.Push(100d, Positions(1f), Valid(true), tracked: true);
            history.Push(200d, Positions(2f), Valid(true), tracked: true);
            history.Push(150d, Positions(3f), Valid(true), tracked: true);
            var output = new Vector3[1];
            var valid = new bool[1];

            Assert.That(
                history.Sample(100d, output, valid),
                Is.EqualTo(HandHistorySampleResult.TargetBeforeOldest));
            Assert.That(
                history.Sample(150d, output, valid),
                Is.EqualTo(HandHistorySampleResult.Success));
            Assert.That(output[0].x, Is.EqualTo(3f));
        }

        [Test]
        public void Push_WrapAround_PreservesLogicalOrdering()
        {
            var history = new TimedHandJointHistory(jointCount: 1, capacity: 3);
            history.Push(100d, Positions(1f), Valid(true), tracked: true);
            history.Push(200d, Positions(2f), Valid(true), tracked: true);
            history.Push(300d, Positions(3f), Valid(true), tracked: true);
            history.Push(400d, Positions(4f), Valid(true), tracked: true);
            var output = new Vector3[1];
            var valid = new bool[1];

            Assert.That(
                history.Sample(150d, output, valid),
                Is.EqualTo(HandHistorySampleResult.TargetBeforeOldest));
            Assert.That(
                history.Sample(250d, output, valid),
                Is.EqualTo(HandHistorySampleResult.Success));
            Assert.That(output[0].x, Is.EqualTo(2.5f).Within(0.0001f));
        }

        [Test]
        public void Sample_EmptyHistory_ReturnsEmptyAndClearsValidity()
        {
            var history = new TimedHandJointHistory(jointCount: 1, capacity: 4);
            var output = new Vector3[1];
            var valid = new[] { true };

            HandHistorySampleResult result = history.Sample(100d, output, valid);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.Empty));
            Assert.That(valid[0], Is.False);
        }

        [Test]
        public void Sample_InvalidTarget_ReturnsInvalidArgumentsAndClearsValidity()
        {
            TimedHandJointHistory history = CreateTwoSnapshotHistory();
            var output = new Vector3[2];
            var valid = new[] { true, true };

            HandHistorySampleResult result = history.Sample(double.NaN, output, valid);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.InvalidArguments));
            Assert.That(valid, Is.EqualTo(new[] { false, false }));
        }

        [Test]
        public void Sample_ShortOutput_ReturnsInvalidArguments()
        {
            TimedHandJointHistory history = CreateTwoSnapshotHistory();

            HandHistorySampleResult result = history.Sample(
                150d,
                new Vector3[1],
                new bool[2]);

            Assert.That(result, Is.EqualTo(HandHistorySampleResult.InvalidArguments));
        }

        [Test]
        public void Push_InvalidInput_DoesNotCreateSnapshot()
        {
            var history = new TimedHandJointHistory(jointCount: 2, capacity: 4);
            history.Push(100d, new Vector3[1], new bool[2], tracked: true);
            var output = new Vector3[2];
            var valid = new bool[2];

            Assert.That(
                history.Sample(100d, output, valid),
                Is.EqualTo(HandHistorySampleResult.Empty));
        }

        private static TimedHandJointHistory CreateTwoSnapshotHistory()
        {
            var history = new TimedHandJointHistory(jointCount: 2, capacity: 4);
            history.Push(100d, Positions(0f, 10f), Valid(true, true), tracked: true);
            history.Push(200d, Positions(10f, 30f), Valid(true, true), tracked: true);
            return history;
        }

        private static Vector3[] Positions(params float[] x)
        {
            var positions = new Vector3[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                positions[i] = new Vector3(x[i], 0f, 0f);
            }
            return positions;
        }

        private static bool[] Valid(params bool[] valid)
        {
            return valid;
        }
    }
}
