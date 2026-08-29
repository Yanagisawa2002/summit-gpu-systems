using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDirectBinning.Tests
{
    public sealed class GpuDirectSpatialBinnerContractTests
    {
        [Test]
        public void TypeIsSealedDisposableAndExposesSingleRecordContract()
        {
            var type = typeof(GpuDirectSpatialBinner);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.True);

            var methods = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name == "Record")
                .ToArray();
            Assert.That(methods, Has.Length.EqualTo(1));

            var method = methods[0];
            Assert.That(method.ReturnType, Is.EqualTo(typeof(void)));
            var parameters = method.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(10));
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
                    typeof(int),
                    typeof(int),
                    typeof(GpuPrimitiveBackend),
                }));
        }

        [Test]
        public void ScanBackendIsTheOnlyOptionalRecordArgument()
        {
            var method = typeof(GpuDirectSpatialBinner).GetMethod("Record");
            Assert.That(method, Is.Not.Null);
            var parameters = method.GetParameters();

            Assert.That(
                parameters.Take(parameters.Length - 1)
                    .All(parameter => !parameter.IsOptional),
                Is.True);
            Assert.That(parameters[parameters.Length - 1].IsOptional, Is.True);
            Assert.That(
                parameters[parameters.Length - 1].DefaultValue,
                Is.EqualTo(GpuPrimitiveBackend.Auto));
        }

        [Test]
        public void GuaranteedInRangeRecordIsExplicitAndShapeCompatible()
        {
            MethodInfo trusted = typeof(GpuDirectSpatialBinner)
                .GetMethod("RecordGuaranteedInRange");
            Assert.That(trusted, Is.Not.Null);
            Assert.That(trusted.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(
                trusted.GetParameters()
                    .Select(parameter => parameter.ParameterType),
                Is.EqualTo(new[]
                {
                    typeof(CommandBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(int),
                    typeof(int),
                    typeof(GpuPrimitiveBackend),
                }));
            Assert.That(
                trusted.GetParameters().Last().DefaultValue,
                Is.EqualTo(GpuPrimitiveBackend.Auto));
        }

        [Test]
        public void TrustedNoDiagnosticClearRecordIsExplicitAndShapeCompatible()
        {
            MethodInfo trusted = typeof(GpuDirectSpatialBinner)
                .GetMethod(
                    "RecordGuaranteedInRangeWithoutDiagnosticClear");
            Assert.That(trusted, Is.Not.Null);
            Assert.That(trusted.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(
                trusted.GetParameters()
                    .Select(parameter => parameter.ParameterType),
                Is.EqualTo(new[]
                {
                    typeof(CommandBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(int),
                    typeof(int),
                    typeof(GpuPrimitiveBackend),
                }));
            ParameterInfo[] parameters = trusted.GetParameters();
            Assert.That(
                parameters.Take(parameters.Length - 1)
                    .All(parameter => !parameter.IsOptional),
                Is.True);
            Assert.That(parameters.Last().IsOptional, Is.True);
            Assert.That(
                parameters.Last().DefaultValue,
                Is.EqualTo(GpuPrimitiveBackend.Auto));
        }

        [TestCase("RecordWithDiscardKey")]
        [TestCase("RecordWithDiscardKeyWithoutDiagnosticClear")]
        public void DiscardKeyRecordsHaveExplicitShape(string methodName)
        {
            MethodInfo method = typeof(GpuDirectSpatialBinner)
                .GetMethod(methodName);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(
                method.GetParameters()
                    .Select(parameter => parameter.ParameterType),
                Is.EqualTo(new[]
                {
                    typeof(CommandBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(GraphicsBuffer),
                    typeof(int),
                    typeof(int),
                    typeof(uint),
                    typeof(GpuPrimitiveBackend),
                }));
            ParameterInfo[] parameters = method.GetParameters();
            Assert.That(
                parameters.Take(parameters.Length - 1)
                    .All(parameter => !parameter.IsOptional),
                Is.True);
            Assert.That(parameters.Last().IsOptional, Is.True);
            Assert.That(
                parameters.Last().DefaultValue,
                Is.EqualTo(GpuPrimitiveBackend.Auto));
        }

        [Test]
        public void ProfilerMarkerConstructorOptionDefaultsToEnabled()
        {
            ConstructorInfo constructor = typeof(GpuDirectSpatialBinner)
                .GetConstructors()
                .Single();
            ParameterInfo markerOption = constructor
                .GetParameters()
                .Single(parameter =>
                    parameter.Name == "emitProfilerMarkers");
            Assert.That(markerOption.ParameterType, Is.EqualTo(typeof(bool)));
            Assert.That(markerOption.IsOptional, Is.True);
            Assert.That(markerOption.DefaultValue, Is.True);
        }

        [Test]
        public void DiagnosticAbiHasTwoStableWords()
        {
            Assert.That(
                GpuDirectSpatialBinner.DiagnosticWordCount,
                Is.EqualTo(2));
            Assert.That(
                GpuDirectSpatialBinner.InvalidKeyCountWord,
                Is.EqualTo(0));
            Assert.That(
                GpuDirectSpatialBinner.ErrorFlagsWord,
                Is.EqualTo(1));
        }

        [Test]
        public void ErrorFlagsUseStableIndependentBits()
        {
            Assert.That(
                (uint)GpuDirectBinningErrorFlags.None,
                Is.Zero);
            Assert.That(
                (uint)GpuDirectBinningErrorFlags.InvalidKeyEncountered,
                Is.EqualTo(1u));
            Assert.That(
                (uint)GpuDirectBinningErrorFlags.ScatterDestinationOutOfRange,
                Is.EqualTo(2u));
            Assert.That(
                GpuDirectBinningErrorFlags.InvalidKeyEncountered &
                GpuDirectBinningErrorFlags.ScatterDestinationOutOfRange,
                Is.EqualTo(GpuDirectBinningErrorFlags.None));
        }

        [Test]
        public void ConstructorRejectsInvalidFixedCapacitiesBeforeGpuUse()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuDirectSpatialBinner(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuDirectSpatialBinner(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuDirectSpatialBinner(-1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuDirectSpatialBinner(1, -1));
        }
    }
}
