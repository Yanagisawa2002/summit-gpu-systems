using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Summit.GpuSensorPipeline.Tests
{
    public sealed class GpuSensorDeterministicGeneratorTests
    {
        [Test]
        public void PublishedDomainConstantsAreStable()
        {
            Assert.That(GpuSensorDeterministicGenerator.StateCount, Is.EqualTo(64));
            Assert.That(GpuSensorDeterministicGenerator.GridAxis, Is.EqualTo(64));
            Assert.That(GpuSensorDeterministicGenerator.BinCount, Is.EqualTo(262144));
            Assert.That(GpuSensorDeterministicGenerator.CoordinateMask, Is.EqualTo(65535));
        }

        [TestCase(0u, 0u)]
        [TestCase(1u, 1u)]
        [TestCase(0x9e3779b9u, 63u)]
        [TestCase(uint.MaxValue, 63u)]
        public void PopulateIsDeterministicAndMatchesElementGenerator(
            uint seed,
            uint logicalState)
        {
            const int count = 1025;
            var firstSamples = new GpuSensorSample[count];
            var firstKeys = new uint[count];
            var secondSamples = new GpuSensorSample[count];
            var secondKeys = new uint[count];

            GpuSensorDeterministicGenerator.Populate(
                firstSamples,
                firstKeys,
                seed,
                logicalState);
            GpuSensorDeterministicGenerator.Populate(
                secondSamples,
                secondKeys,
                seed,
                logicalState);

            Assert.That(secondSamples, Is.EqualTo(firstSamples));
            Assert.That(secondKeys, Is.EqualTo(firstKeys));
            for (var index = 0; index < count; index++)
            {
                Assert.That(
                    InvokeGenerateSample(index, seed, logicalState),
                    Is.EqualTo(firstSamples[index]),
                    $"GenerateSample differs from Populate at index {index}.");
            }
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(0x12345678u)]
        [TestCase(uint.MaxValue)]
        public void ExactlySixtyFourLogicalStatesAreValidAndDistinct(uint seed)
        {
            const int count = 257;
            var signatures = new string[GpuSensorDeterministicGenerator.StateCount];
            for (uint state = 0u;
                 state < GpuSensorDeterministicGenerator.StateCount;
                 state++)
            {
                var samples = new GpuSensorSample[count];
                var keys = new uint[count];
                GpuSensorDeterministicGenerator.Populate(
                    samples,
                    keys,
                    seed,
                    state);
                signatures[state] = string.Join(
                    ":",
                    ReadWords(samples[0]).Concat(new[] { keys[0] }));
            }

            Assert.That(
                signatures.Distinct().Count(),
                Is.EqualTo(GpuSensorDeterministicGenerator.StateCount),
                "The 64-state cycle must not collapse two logical frames.");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GpuSensorDeterministicGenerator.Populate(
                    new GpuSensorSample[1],
                    new uint[1],
                    seed,
                    GpuSensorDeterministicGenerator.StateCount));
        }

        [Test]
        public void EveryGeneratedKeyIsProvablyInRangeAndMatchesGridMapping()
        {
            const int count = 4097;
            uint[] seeds = { 0u, 1u, 0x9e3779b9u, uint.MaxValue };
            uint[] states = { 0u, 1u, 31u, 63u };

            foreach (uint seed in seeds)
            foreach (uint state in states)
            {
                var samples = new GpuSensorSample[count];
                var keys = new uint[count];
                GpuSensorDeterministicGenerator.Populate(samples, keys, seed, state);

                for (var index = 0; index < count; index++)
                {
                    uint[] words = ReadWords(samples[index]);
                    Assert.That(words[0], Is.LessThanOrEqualTo(
                        (uint)GpuSensorDeterministicGenerator.CoordinateMask));
                    Assert.That(words[1], Is.LessThanOrEqualTo(
                        (uint)GpuSensorDeterministicGenerator.CoordinateMask));
                    Assert.That(words[2], Is.LessThanOrEqualTo(
                        (uint)GpuSensorDeterministicGenerator.CoordinateMask));

                    uint independentlyComputed = ComputeGridKey(
                        words[0], words[1], words[2]);
                    Assert.That(keys[index], Is.EqualTo(independentlyComputed));
                    Assert.That(keys[index], Is.EqualTo(InvokeComputeKey(samples[index])));
                    Assert.That(keys[index], Is.LessThan(
                        (uint)GpuSensorDeterministicGenerator.BinCount));
                }
            }
        }

        [Test]
        public void PopulateRejectsNullOrMismatchedStorage()
        {
            var samples = new GpuSensorSample[2];
            var keys = new uint[2];

            Assert.Throws<ArgumentNullException>(() =>
                GpuSensorDeterministicGenerator.Populate(null, keys, 1u, 0u));
            Assert.Throws<ArgumentNullException>(() =>
                GpuSensorDeterministicGenerator.Populate(samples, null, 1u, 0u));
            Assert.Throws<ArgumentException>(() =>
                GpuSensorDeterministicGenerator.Populate(
                    samples,
                    new uint[1],
                    1u,
                    0u));
        }

        private static GpuSensorSample InvokeGenerateSample(
            int index,
            uint seed,
            uint logicalState)
        {
            MethodInfo method = typeof(GpuSensorDeterministicGenerator)
                .GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Single(candidate => candidate.Name == "GenerateSample");
            ParameterInfo[] parameters = method.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(3));

            var arguments = new object[3];
            for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
            {
                string name = parameters[parameterIndex].Name.ToLowerInvariant();
                if (name.Contains("index"))
                {
                    arguments[parameterIndex] = parameters[parameterIndex].ParameterType == typeof(int)
                        ? (object)index
                        : (uint)index;
                }
                else if (name.Contains("seed"))
                {
                    arguments[parameterIndex] = seed;
                }
                else
                {
                    arguments[parameterIndex] = logicalState;
                }
            }

            return (GpuSensorSample)method.Invoke(null, arguments);
        }

        private static uint InvokeComputeKey(GpuSensorSample sample)
        {
            MethodInfo method = typeof(GpuSensorDeterministicGenerator)
                .GetMethod(
                    "ComputeKey",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { typeof(GpuSensorSample) },
                    null);
            Assert.That(method, Is.Not.Null);
            return (uint)method.Invoke(null, new object[] { sample });
        }

        private static uint ComputeGridKey(uint x, uint y, uint z)
        {
            const int coordinateBits = 16;
            const int gridBits = 6;
            const int shift = coordinateBits - gridBits;
            uint cellX = x >> shift;
            uint cellY = y >> shift;
            uint cellZ = z >> shift;
            return cellX |
                   (cellY << gridBits) |
                   (cellZ << (2 * gridBits));
        }

        private static uint[] ReadWords(GpuSensorSample sample)
        {
            return typeof(GpuSensorSample)
                .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(member =>
                    member is FieldInfo field && field.FieldType == typeof(uint) ||
                    member is PropertyInfo property && property.PropertyType == typeof(uint))
                .OrderBy(member => member.MetadataToken)
                .Select(member => member is FieldInfo field
                    ? (uint)field.GetValue(sample)
                    : (uint)((PropertyInfo)member).GetValue(sample))
                .ToArray();
        }
    }
}
