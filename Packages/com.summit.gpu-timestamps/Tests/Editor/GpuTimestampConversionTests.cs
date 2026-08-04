using System;
using NUnit.Framework;

namespace Summit.GpuTimestamps.Tests
{
    [TestFixture]
    public sealed class GpuTimestampConversionTests
    {
        [TestCase(100UL, 100UL, 1000UL, 0L)]
        [TestCase(100UL, 101UL, 1000UL, 1000000L)]
        [TestCase(1000UL, 1250UL, 10000000UL, 25000L)]
        [TestCase(9000000000UL, 9016666667UL, 1000000000UL, 16666667L)]
        public void TicksToNanoseconds_MatchesReviewedVectors(
            ulong beginTicks,
            ulong endTicks,
            ulong frequency,
            long expectedNanoseconds)
        {
            Assert.That(endTicks, Is.GreaterThanOrEqualTo(beginTicks));
            long actual = GpuTimestampSession.TicksToNanoseconds(
                endTicks - beginTicks,
                frequency);
            Assert.That(actual, Is.EqualTo(expectedNanoseconds));
        }

        [Test]
        public void TicksToNanoseconds_UsesMidpointAwayFromZero()
        {
            Assert.That(
                GpuTimestampSession.TicksToNanoseconds(1UL, 2000000000UL),
                Is.EqualTo(1L));
            Assert.That(
                GpuTimestampSession.TicksToNanoseconds(1UL, 3000000000UL),
                Is.EqualTo(0L));
            Assert.That(
                GpuTimestampSession.TicksToNanoseconds(2UL, 3000000000UL),
                Is.EqualTo(1L));
        }

        [Test]
        public void TicksToNanoseconds_LargeValidValueDoesNotOverflowIntermediate()
        {
            const ulong elapsedTicks = 9000000000000000000UL;
            const ulong frequency = 1000000000UL;
            const long expectedNanoseconds = 9000000000000000000L;

            Assert.That(
                GpuTimestampSession.TicksToNanoseconds(elapsedTicks, frequency),
                Is.EqualTo(expectedNanoseconds));
        }

        [Test]
        public void TicksToNanoseconds_RejectsZeroFrequency()
        {
            Assert.Throws<DivideByZeroException>(
                () => GpuTimestampSession.TicksToNanoseconds(1UL, 0UL));
        }

        [Test]
        public void TicksToNanoseconds_RejectsDurationOutsideInt64()
        {
            Assert.Throws<OverflowException>(
                () => GpuTimestampSession.TicksToNanoseconds(ulong.MaxValue, 1UL));
        }
    }
}
