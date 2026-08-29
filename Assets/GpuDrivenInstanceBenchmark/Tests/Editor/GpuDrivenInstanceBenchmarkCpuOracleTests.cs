using NUnit.Framework;
using Summit.GpuDrivenInstanceBenchmark;
using Summit.GpuDrivenInstances;
using UnityEngine;

namespace Summit.GpuDrivenInstance.Benchmark.Tests
{
    public sealed class GpuDrivenInstanceBenchmarkCpuOracleTests
    {
        [Test]
        public void VisibleOnlyDropsRejectedPairsButPreservesDraws()
        {
            CreateInput(
                out GpuInstanceState[] instances,
                out Vector4[] planes,
                out Vector4[] views,
                out GpuDrawTemplate[] draws);

            GpuDrivenInstanceExpectedResult culledTail =
                GpuDrivenInstanceBenchmarkCpuOracle.Build(
                    instances,
                    planes,
                    views,
                    draws,
                    GpuDrivenInstanceOutputMode.CulledTail);
            GpuDrivenInstanceExpectedResult visibleOnly =
                GpuDrivenInstanceBenchmarkCpuOracle.Build(
                    instances,
                    planes,
                    views,
                    draws,
                    GpuDrivenInstanceOutputMode.VisibleOnly);

            Assert.That(culledTail.Counts, Has.Length.EqualTo(17));
            Assert.That(visibleOnly.Counts, Has.Length.EqualTo(16));
            Assert.That(
                culledTail.GroupedInstanceIndices,
                Has.Length.EqualTo(200));
            Assert.That(
                visibleOnly.GroupedInstanceIndices,
                Has.Length.EqualTo(50));
            Assert.That(
                culledTail.IndirectArguments,
                Is.EqualTo(visibleOnly.IndirectArguments));
            Assert.That(
                culledTail.ContractViolationCount,
                Is.EqualTo(0u));
            Assert.That(
                visibleOnly.ContractViolationCount,
                Is.EqualTo(0u));
        }

        [Test]
        public void ValidationAcceptsOracleOutputAndRejectsCorruption()
        {
            CreateInput(
                out GpuInstanceState[] instances,
                out Vector4[] planes,
                out Vector4[] views,
                out GpuDrawTemplate[] draws);
            GpuDrivenInstanceExpectedResult expected =
                GpuDrivenInstanceBenchmarkCpuOracle.Build(
                    instances,
                    planes,
                    views,
                    draws,
                    GpuDrivenInstanceOutputMode.VisibleOnly);
            var diagnostics = new[]
            {
                expected.ContractViolationCount,
                expected.ErrorFlags,
            };

            Assert.That(
                GpuDrivenInstanceBenchmarkCpuOracle.Validate(
                    expected,
                    expected.Counts,
                    expected.Offsets,
                    expected.GroupedInstanceIndices,
                    expected.IndirectArguments,
                    diagnostics,
                    out string message,
                    out string hash),
                Is.True,
                message);
            Assert.That(hash, Is.EqualTo(expected.ResultHash));

            uint[] corruptCounts =
                (uint[])expected.Counts.Clone();
            corruptCounts[0]++;
            Assert.That(
                GpuDrivenInstanceBenchmarkCpuOracle.Validate(
                    expected,
                    corruptCounts,
                    expected.Offsets,
                    expected.GroupedInstanceIndices,
                    expected.IndirectArguments,
                    diagnostics,
                    out message,
                    out _),
                Is.False);
            StringAssert.Contains("Count mismatch", message);
        }

        private static void CreateInput(
            out GpuInstanceState[] instances,
            out Vector4[] planes,
            out Vector4[] views,
            out GpuDrawTemplate[] draws)
        {
            instances = new GpuInstanceState[100];
            planes = new Vector4[12];
            views = new Vector4[2];
            draws = new GpuDrawTemplate[8];
            int visible = GpuDrivenInstanceInputGenerator.Populate(
                instances,
                planes,
                views,
                draws,
                "visible25",
                20260829);
            Assert.That(visible, Is.EqualTo(25));
        }
    }
}
