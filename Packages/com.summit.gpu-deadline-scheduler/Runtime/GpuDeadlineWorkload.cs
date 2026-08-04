using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDeadlineScheduler
{
    public sealed class GpuDeadlineWorkload : IDisposable
    {
        public const int ThreadGroupSize = 256;
        public const int DigestStride = 16;

        private const string ResourcePath =
            "GpuDeadlineScheduler/GpuDeadlineWorkload";

        private static readonly int JobIdId = Shader.PropertyToID("_JobId");
        private static readonly int LogicalStateId =
            Shader.PropertyToID("_LogicalState");
        private static readonly int SeedId = Shader.PropertyToID("_Seed");
        private static readonly int WorkItemCountId =
            Shader.PropertyToID("_WorkItemCount");
        private static readonly int IterationsId =
            Shader.PropertyToID("_Iterations");
        private static readonly int PressureItemCountId =
            Shader.PropertyToID("_PressureItemCount");
        private static readonly int FrameControlId =
            Shader.PropertyToID("_FrameControl");
        private static readonly int JobOutputsId =
            Shader.PropertyToID("_JobOutputs");
        private static readonly int JobDigestsId =
            Shader.PropertyToID("_JobDigests");
        private static readonly int PressureOutputsId =
            Shader.PropertyToID("_PressureOutputs");

        private readonly ComputeShader shader;
        private readonly int jobKernel;
        private readonly int pressureKernel;
        private readonly GraphicsBuffer[] jobOutputs;
        private readonly GraphicsBuffer[] jobDigests;
        private bool disposed;

        public GpuDeadlineWorkload(
            int jobCapacity,
            int workItemCapacity,
            int pressureItemCapacity,
            ComputeShader shader = null)
        {
            if (jobCapacity < 1 || jobCapacity > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(jobCapacity));
            }
            if (workItemCapacity < 1 || workItemCapacity > 1048576)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(workItemCapacity));
            }
            if (pressureItemCapacity < 1 ||
                pressureItemCapacity > 1048576)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pressureItemCapacity));
            }

            ComputeShader selected = shader != null
                ? shader
                : Resources.Load<ComputeShader>(ResourcePath);
            if (selected == null)
            {
                throw new InvalidOperationException(
                    $"Compute shader resource '{ResourcePath}' was not found.");
            }

            GraphicsBuffer selectedControlSource = null;
            GraphicsBuffer selectedControlResident = null;
            GraphicsBuffer[] selectedOutputs = null;
            GraphicsBuffer[] selectedDigests = null;
            GraphicsBuffer selectedPressure = null;
            try
            {
                selectedControlSource = CreateBuffer(
                    1,
                    sizeof(uint),
                    "GPU Deadline Control Source",
                    GraphicsBuffer.Target.CopySource);
                selectedControlResident = CreateBuffer(
                    1,
                    sizeof(uint),
                    "GPU Deadline Resident Control",
                    GraphicsBuffer.Target.CopyDestination);
                selectedOutputs = new GraphicsBuffer[jobCapacity];
                selectedDigests = new GraphicsBuffer[jobCapacity];
                for (int slot = 0; slot < jobCapacity; slot++)
                {
                    selectedOutputs[slot] = CreateBuffer(
                        workItemCapacity,
                        DigestStride,
                        "GPU Deadline Job Output " + slot);
                    selectedDigests[slot] = CreateBuffer(
                        1,
                        DigestStride,
                        "GPU Deadline Job Digest " + slot);
                }
                selectedPressure = CreateBuffer(
                    pressureItemCapacity,
                    sizeof(uint),
                    "GPU Deadline Graphics Pressure");
                selectedControlSource.SetData(new[] { 0xC001D00Du });
            }
            catch
            {
                selectedControlSource?.Dispose();
                selectedControlResident?.Dispose();
                DisposeBuffers(selectedOutputs);
                DisposeBuffers(selectedDigests);
                selectedPressure?.Dispose();
                throw;
            }

            JobCapacity = jobCapacity;
            WorkItemCapacity = workItemCapacity;
            PressureItemCapacity = pressureItemCapacity;
            this.shader = selected;
            jobKernel = selected.FindKernel("DeadlineWork");
            pressureKernel = selected.FindKernel("GraphicsPressure");
            ControlSource = selectedControlSource;
            ControlResident = selectedControlResident;
            jobOutputs = selectedOutputs;
            jobDigests = selectedDigests;
            PressureOutputs = selectedPressure;
        }

        public int JobCapacity { get; }

        public int WorkItemCapacity { get; }

        public int PressureItemCapacity { get; }

        public GraphicsBuffer ControlSource { get; }

        public GraphicsBuffer ControlResident { get; }

        public GraphicsBuffer PressureOutputs { get; }

        public long ResidentBytes => checked(
            sizeof(uint) * 2L +
            (long)JobCapacity * WorkItemCapacity * DigestStride +
            (long)JobCapacity * DigestStride +
            (long)PressureItemCapacity * sizeof(uint));

        public GraphicsBuffer GetJobOutputBuffer(int jobSlot)
        {
            ValidateJobSlot(jobSlot);
            return jobOutputs[jobSlot];
        }

        public GraphicsBuffer GetJobDigestBuffer(int jobSlot)
        {
            ValidateJobSlot(jobSlot);
            return jobDigests[jobSlot];
        }

        public void RecordCopyStage(CommandBuffer commands)
        {
            ValidateCommands(commands);
            commands.CopyBuffer(ControlSource, ControlResident);
        }

        public void RecordJob(
            CommandBuffer commands,
            GpuDeadlineJob job,
            int jobSlot,
            uint logicalState)
        {
            ValidateCommands(commands);
            if (jobSlot < 0 || jobSlot >= JobCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(jobSlot));
            }
            if (job.WorkItemCount > WorkItemCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(job));
            }

            commands.SetComputeIntParam(shader, JobIdId, job.JobId);
            commands.SetComputeIntParam(
                shader,
                LogicalStateId,
                unchecked((int)logicalState));
            commands.SetComputeIntParam(
                shader,
                SeedId,
                unchecked((int)job.Seed));
            commands.SetComputeIntParam(
                shader,
                WorkItemCountId,
                job.WorkItemCount);
            commands.SetComputeIntParam(shader, IterationsId, job.Iterations);
            commands.SetComputeBufferParam(
                shader,
                jobKernel,
                FrameControlId,
                ControlResident);
            commands.SetComputeBufferParam(
                shader,
                jobKernel,
                JobOutputsId,
                jobOutputs[jobSlot]);
            commands.SetComputeBufferParam(
                shader,
                jobKernel,
                JobDigestsId,
                jobDigests[jobSlot]);
            commands.DispatchCompute(
                shader,
                jobKernel,
                DivideRoundUp(job.WorkItemCount, ThreadGroupSize),
                1,
                1);
        }

        public void RecordGraphicsPressure(
            CommandBuffer commands,
            int itemCount,
            int iterations,
            uint logicalState)
        {
            ValidateCommands(commands);
            if (itemCount < 1 || itemCount > PressureItemCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(itemCount));
            }
            if (iterations < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(iterations));
            }

            commands.SetComputeIntParam(shader, PressureItemCountId, itemCount);
            commands.SetComputeIntParam(shader, IterationsId, iterations);
            commands.SetComputeIntParam(
                shader,
                LogicalStateId,
                unchecked((int)logicalState));
            commands.SetComputeBufferParam(
                shader,
                pressureKernel,
                FrameControlId,
                ControlResident);
            commands.SetComputeBufferParam(
                shader,
                pressureKernel,
                PressureOutputsId,
                PressureOutputs);
            commands.DispatchCompute(
                shader,
                pressureKernel,
                DivideRoundUp(itemCount, ThreadGroupSize),
                1,
                1);
        }

        public static uint[] ExpectedDigest(
            GpuDeadlineJob job,
            uint logicalState,
            uint frameControl = 0xC001D00Du)
        {
            uint value = RunWork(
                unchecked((uint)job.JobId),
                logicalState,
                0u,
                unchecked((uint)job.Iterations),
                frameControl,
                job.Seed);
            return new[]
            {
                value,
                value ^ 0xA511E9B3u,
                unchecked(value + (uint)job.JobId),
                unchecked((uint)job.Iterations)
            };
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            ControlSource.Dispose();
            ControlResident.Dispose();
            DisposeBuffers(jobOutputs);
            DisposeBuffers(jobDigests);
            PressureOutputs.Dispose();
        }

        private static uint RunWork(
            uint jobId,
            uint logicalState,
            uint threadIndex,
            uint iterations,
            uint frameControl,
            uint seed)
        {
            uint value = Mix(
                seed ^
                unchecked(jobId * 0x9E3779B9u) ^
                unchecked(logicalState * 0x85EBCA6Bu) ^
                threadIndex ^
                frameControl);
            for (uint iteration = 0u; iteration < iterations; iteration++)
            {
                value = Mix(
                    value ^ unchecked(iteration * 0xC2B2AE35u));
            }
            return value;
        }

        private static uint Mix(uint value)
        {
            value ^= value >> 16;
            value = unchecked(value * 0x7FEB352Du);
            value ^= value >> 15;
            value = unchecked(value * 0x846CA68Bu);
            value ^= value >> 16;
            return value;
        }

        private static GraphicsBuffer CreateBuffer(
            int count,
            int stride,
            string name,
            GraphicsBuffer.Target extraTarget = 0)
        {
            return new GraphicsBuffer(
                GraphicsBuffer.Target.Structured | extraTarget,
                count,
                stride)
            {
                name = name
            };
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return checked((value + divisor - 1) / divisor);
        }

        private void ValidateCommands(CommandBuffer commands)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(GpuDeadlineWorkload));
            }
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
        }

        private void ValidateJobSlot(int jobSlot)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(GpuDeadlineWorkload));
            }
            if (jobSlot < 0 || jobSlot >= JobCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(jobSlot));
            }
        }

        private static void DisposeBuffers(GraphicsBuffer[] buffers)
        {
            if (buffers == null)
            {
                return;
            }
            foreach (GraphicsBuffer buffer in buffers)
            {
                buffer?.Dispose();
            }
        }
    }
}
