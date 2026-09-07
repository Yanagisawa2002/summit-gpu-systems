using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline.Tests
{
    public sealed class GpuSensorPipelineContractTests
    {
        public static IEnumerable<Type> FourWordTypes
        {
            get
            {
                yield return typeof(GpuSensorSample);
                yield return typeof(GpuSensorRangeQuery);
                yield return typeof(GpuSensorQueryDigest);
            }
        }

        [TestCaseSource(nameof(FourWordTypes))]
        public void DataContractIsExactlyFourUintWords(Type type)
        {
            Assert.That(Marshal.SizeOf(type), Is.EqualTo(4 * sizeof(uint)));
            Assert.That(
                GetPublicUintMembers(type).Count,
                Is.EqualTo(4),
                $"{type.Name} must expose its four ABI words for auditability.");
            Assert.That(
                type.GetInterfaces().Any(candidate =>
                    candidate.IsGenericType &&
                    candidate.GetGenericTypeDefinition() ==
                    typeof(IEquatable<>) &&
                    candidate.GetGenericArguments()[0] == type),
                Is.True,
                $"{type.Name} must provide typed value equality.");
        }

        [TestCaseSource(nameof(FourWordTypes))]
        public void FourWordConstructorAndEqualityAreValueBased(Type type)
        {
            object first = CreateFourWordValue(type, 1u, 2u, 3u, 4u);
            object same = CreateFourWordValue(type, 1u, 2u, 3u, 4u);
            object different = CreateFourWordValue(type, 1u, 2u, 3u, 5u);

            Assert.That(ReadWords(first), Is.EqualTo(new[] { 1u, 2u, 3u, 4u }));
            Assert.That(first, Is.EqualTo(same));
            Assert.That(first.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(first, Is.Not.EqualTo(different));
        }

        [Test]
        public void PipelineIsSealedAndDisposable()
        {
            Type type = typeof(GpuSensorPipeline);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.True);
        }

        [Test]
        public void ConstructorDefaultsToExplicitWaveBackendAndMarkers()
        {
            ConstructorInfo constructor = typeof(GpuSensorPipeline)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Single();
            ParameterInfo[] parameters = constructor.GetParameters();

            Assert.That(parameters.Select(parameter => parameter.ParameterType),
                Is.EqualTo(new[]
                {
                    typeof(int),
                    typeof(int),
                    typeof(GpuPrimitiveBackend),
                    typeof(bool),
                    typeof(ComputeShader),
                    typeof(GpuSensorQueryBackend),
                    typeof(int),
                }));
            Assert.That(parameters[0].IsOptional, Is.False);
            Assert.That(parameters[1].IsOptional, Is.False);
            Assert.That(parameters[2].DefaultValue, Is.EqualTo(GpuPrimitiveBackend.WaveOps));
            Assert.That(parameters[3].DefaultValue, Is.True);
            Assert.That(parameters[4].DefaultValue, Is.Null);
            Assert.That(parameters[5].DefaultValue, Is.EqualTo(GpuSensorQueryBackend.CellSerial));
            Assert.That(parameters[6].DefaultValue, Is.EqualTo(0));
        }

        [Test]
        public void RecordAndUploadContractsAreUnambiguous()
        {
            AssertMethod(
                "RecordCpuProduced",
                typeof(void),
                typeof(CommandBuffer),
                typeof(GpuSensorSample[]),
                typeof(uint[]),
                typeof(int),
                typeof(int),
                typeof(uint));
            AssertMethod(
                "RecordGpuProduced",
                typeof(void),
                typeof(CommandBuffer),
                typeof(uint),
                typeof(uint),
                typeof(int),
                typeof(int));
            AssertMethod(
                "RecordGpuProducedRebuiltPerSensor",
                typeof(void),
                typeof(CommandBuffer),
                typeof(uint),
                typeof(uint),
                typeof(int),
                typeof(int),
                typeof(int));
            AssertMethod(
                "RecordGpuProducedSharedSensorIndex",
                typeof(void),
                typeof(CommandBuffer),
                typeof(uint),
                typeof(uint),
                typeof(int),
                typeof(int),
                typeof(int));
            AssertMethod(
                "RecordGpuProducedQuantizedView",
                typeof(void),
                typeof(CommandBuffer),
                typeof(uint),
                typeof(uint),
                typeof(int),
                typeof(int));
            AssertMethod(
                "RecordValidateKeys",
                typeof(void),
                typeof(CommandBuffer),
                typeof(int));
            AssertMethod(
                "RecordClearComparison",
                typeof(void),
                typeof(CommandBuffer));
            AssertMethod(
                "RecordCompareDigests",
                typeof(void),
                typeof(CommandBuffer),
                typeof(GraphicsBuffer),
                typeof(GraphicsBuffer),
                typeof(int));
            AssertMethod("SetStableIds", typeof(void), typeof(uint[]));
            AssertMethod("SetQueries", typeof(void), typeof(GpuSensorRangeQuery[]));
        }

        [Test]
        public void PublicResourceSurfaceIncludesCapacitiesBytesAndBuffers()
        {
            PropertyInfo[] properties = typeof(GpuSensorPipeline)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public);

            Assert.That(
                properties.Any(property =>
                    property.PropertyType == typeof(int) &&
                    NameContains(property.Name, "element", "capacity")),
                Is.True,
                "The fixed element capacity must be observable.");
            Assert.That(
                properties.Any(property =>
                    property.PropertyType == typeof(int) &&
                    NameContains(property.Name, "query", "capacity")),
                Is.True,
                "The fixed query capacity must be observable.");
            Assert.That(
                properties.Any(property =>
                    IsIntegralByteType(property.PropertyType) &&
                    property.Name.IndexOf("byte", StringComparison.OrdinalIgnoreCase) >= 0),
                Is.True,
                "Case-resident allocation bytes must be observable.");
            Assert.That(
                properties.Count(property => property.PropertyType == typeof(GraphicsBuffer)),
                Is.GreaterThanOrEqualTo(4),
                "Tests and benchmarks require read-only access to owned GPU outputs.");
            Assert.That(
                properties.Where(property => property.PropertyType == typeof(GraphicsBuffer))
                    .All(property => property.CanRead && !property.CanWrite),
                Is.True,
                "Owned GPU buffers must not be replaceable by callers.");
        }

        [Test]
        public void ConstructorRejectsNonPositiveFixedCapacitiesBeforeGpuUse()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuSensorPipeline(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuSensorPipeline(-1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuSensorPipeline(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuSensorPipeline(1, -1));
        }

        [Test]
        public void AutoBackendIsRejectedBeforeGpuWork()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new GpuSensorPipeline(
                    1,
                    1,
                    GpuPrimitiveBackend.Auto,
                    false));
        }


        private static void AssertMethod(string name, Type returnType, params Type[] parameterTypes)
        {
            MethodInfo method = typeof(GpuSensorPipeline).GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public,
                null,
                parameterTypes,
                null);
            Assert.That(method, Is.Not.Null, $"Missing public {name} contract.");
            Assert.That(method.ReturnType, Is.EqualTo(returnType));
            Assert.That(
                method.GetParameters().All(parameter => !parameter.IsOptional),
                Is.True,
                $"{name} must not conceal measurement-affecting defaults.");
        }

        private static object CreateFourWordValue(
            Type type,
            uint first,
            uint second,
            uint third,
            uint fourth)
        {
            ConstructorInfo constructor = type.GetConstructor(new[]
            {
                typeof(uint), typeof(uint), typeof(uint), typeof(uint),
            });
            Assert.That(constructor, Is.Not.Null, $"{type.Name} lacks its four-word constructor.");
            return constructor.Invoke(new object[] { first, second, third, fourth });
        }

        private static IReadOnlyList<MemberInfo> GetPublicUintMembers(Type type)
        {
            return type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(member =>
                    member is FieldInfo field && field.FieldType == typeof(uint) ||
                    member is PropertyInfo property && property.PropertyType == typeof(uint))
                .OrderBy(member => member.MetadataToken)
                .ToArray();
        }

        private static uint[] ReadWords(object value)
        {
            return GetPublicUintMembers(value.GetType())
                .Select(member => member is FieldInfo field
                    ? (uint)field.GetValue(value)
                    : (uint)((PropertyInfo)member).GetValue(value))
                .ToArray();
        }

        private static bool NameContains(string name, params string[] terms)
        {
            return terms.All(term =>
                name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsIntegralByteType(Type type)
        {
            return type == typeof(int) || type == typeof(uint) ||
                   type == typeof(long) || type == typeof(ulong);
        }
    }
}
