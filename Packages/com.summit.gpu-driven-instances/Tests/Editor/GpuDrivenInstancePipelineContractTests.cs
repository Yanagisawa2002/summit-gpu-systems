using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDrivenInstances.Tests
{
    public sealed class GpuDrivenInstancePipelineContractTests
    {
        [Test]
        public void DataAbiUsesExplicitStableStrides()
        {
            Assert.That(
                Marshal.SizeOf<GpuInstanceState>(),
                Is.EqualTo(GpuInstanceState.Stride));
            Assert.That(GpuInstanceState.Stride, Is.EqualTo(48));
            Assert.That(
                Marshal.SizeOf<GpuDrawTemplate>(),
                Is.EqualTo(GpuDrawTemplate.Stride));
            Assert.That(GpuDrawTemplate.Stride, Is.EqualTo(16));
        }

        [Test]
        public void PipelineAbiUsesOneCulledBinAndIndexedDrawArguments()
        {
            Assert.That(
                GpuDrivenInstancePipeline.FrustumPlaneCount,
                Is.EqualTo(6));
            Assert.That(
                GpuDrivenInstancePipeline.IndirectArgumentWordCount,
                Is.EqualTo(5));
            Assert.That(
                GpuDrivenInstancePipeline.DiagnosticWordCount,
                Is.EqualTo(2));
            Assert.That(
                GpuDrivenInstancePipeline.GetVisibleBinCount(3, 7),
                Is.EqualTo(21));
            Assert.That(
                GpuDrivenInstancePipeline.GetCulledBinIndex(3, 7),
                Is.EqualTo(21));
            Assert.That(
                GpuDrivenInstancePipeline.GetOutputBinCount(
                    3,
                    7,
                    GpuDrivenInstanceOutputMode.CulledTail),
                Is.EqualTo(22));
            Assert.That(
                GpuDrivenInstancePipeline.GetOutputBinCount(
                    3,
                    7,
                    GpuDrivenInstanceOutputMode.VisibleOnly),
                Is.EqualTo(21));
        }

        [Test]
        public void ErrorFlagsUseStableIndependentBits()
        {
            uint[] bits = Enum
                .GetValues(typeof(GpuDrivenInstanceErrorFlags))
                .Cast<GpuDrivenInstanceErrorFlags>()
                .Where(value => value != GpuDrivenInstanceErrorFlags.None)
                .Select(value => (uint)value)
                .ToArray();
            Assert.That(bits, Is.EqualTo(new[] { 1u, 2u, 4u, 8u }));
            Assert.That(
                bits.Aggregate(0u, (combined, bit) => combined | bit),
                Is.EqualTo(15u));
        }

        [Test]
        public void RecordHasOneExplicitCommandBufferContract()
        {
            MethodInfo[] records = typeof(GpuDrivenInstancePipeline)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name == "Record")
                .ToArray();
            Assert.That(records, Has.Length.EqualTo(1));
            ParameterInfo[] parameters = records[0].GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(15));
            Assert.That(
                parameters.Select(parameter => parameter.ParameterType),
                Is.EqualTo(new[]
                {
                    typeof(CommandBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(int),
                    typeof(int),
                    typeof(int),
                    typeof(GpuPrimitiveBackend),
                    typeof(GpuDrivenInstanceOutputMode),
                }));
            Assert.That(parameters[parameters.Length - 2].IsOptional, Is.True);
            Assert.That(
                parameters[parameters.Length - 2].DefaultValue,
                Is.EqualTo(GpuPrimitiveBackend.Auto));
            Assert.That(parameters.Last().IsOptional, Is.True);
            Assert.That(
                parameters.Last().DefaultValue,
                Is.EqualTo(GpuDrivenInstanceOutputMode.CulledTail));
            Assert.That(
                parameters.Take(parameters.Length - 2)
                    .All(parameter => !parameter.IsOptional),
                Is.True);
        }

        [Test]
        public void ConstructorRejectsInvalidCapacitiesBeforeGpuUse()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuDrivenInstancePipeline(0, 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuDrivenInstancePipeline(1, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuDrivenInstancePipeline(1, 33, 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuDrivenInstancePipeline(1, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuDrivenInstancePipeline(
                    global::Summit.GpuPrimitives.GpuPrimitives
                        .MaxElementCount,
                    2,
                    1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuDrivenInstancePipeline(
                    1,
                    2,
                    global::Summit.GpuPrimitives.GpuPrimitives
                        .MaxElementCount));
        }

        [Test]
        public void BinHelpersRejectInvalidShapes()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuDrivenInstancePipeline.GetVisibleBinCount(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuDrivenInstancePipeline.GetVisibleBinCount(33, 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuDrivenInstancePipeline.GetVisibleBinCount(1, 0));
            Assert.Throws<OverflowException>(
                () => GpuDrivenInstancePipeline.GetVisibleBinCount(
                    32,
                    int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuDrivenInstancePipeline.GetOutputBinCount(
                    1,
                    1,
                    (GpuDrivenInstanceOutputMode)99));
        }
    }
}
