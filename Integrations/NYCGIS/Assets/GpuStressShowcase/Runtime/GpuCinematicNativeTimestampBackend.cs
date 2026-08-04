using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;

internal enum GpuCinematicTimestampKind
{
    WarmupControl,
    WarmupHero,
    MeasurementControl,
    MeasurementHero
}

internal struct GpuCinematicTimestampEvidence
{
    public GpuCinematicTimestampKind Kind;
    public int SampleIndex;
    public uint Flags;
    public string Status;
    public bool Valid;
    public ulong Token;
    public ulong UserTag;
    public int SourceFrame;
    public int ResultFrame;
    public int PendingFrames;
    public ulong BeginTicks;
    public ulong EndTicks;
    public ulong ElapsedTicks;
    public ulong TimestampFrequency;
    public long ElapsedNanoseconds;
    public double ElapsedMilliseconds;
    public ulong FenceValue;
    public uint DeviceGeneration;
    public int SrpHeroCallbacks;
}

internal sealed class GpuCinematicNativeTimestampBackend : IDisposable
{
    private const int InitialPreparedScopes = 64;
    private const uint RequiredCapabilities = 0x1Fu;
    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private struct PendingSample
    {
        public GpuTimestampToken Token;
        public GpuCinematicTimestampKind Kind;
        public int SampleIndex;
        public int EvidenceIndex;
        public int SrpHeroCallbacks;
    }

    private readonly GpuPrimitiveNativeTimestampBackend backend;
    private readonly CommandBuffer[] beginCommands;
    private readonly CommandBuffer[] endCommands;
    private readonly List<PendingSample> pending =
        new List<PendingSample>(InitialPreparedScopes);
    private readonly List<GpuCinematicTimestampEvidence> evidence =
        new List<GpuCinematicTimestampEvidence>(4096);
    private int preparedScopeCount;

    private ulong nextUserTag = 1UL;
    private GpuTimestampToken activeHeroToken;
    private GpuCinematicTimestampKind activeHeroKind;
    private int activeHeroSampleIndex = -1;
    private int activeHeroEvidenceIndex = -1;
    private int activeHeroSrpCallbacks;
    private bool activeHeroBegun;
    private bool disposed;
    private bool operational = true;
    private bool orderingDiscriminatorPassed;
    private int acquireFailures;
    private int preparedScopeFailures;
    private int resultFailures;
    private int timeouts;
    private int peakActiveSamples;
    private int warmupControlSubmitted;
    private int warmupControlReady;
    private int warmupControlValid;
    private int warmupHeroSubmitted;
    private int warmupHeroReady;
    private int warmupHeroValid;
    private int measurementControlSubmitted;
    private int measurementControlReady;
    private int measurementControlValid;
    private int measurementHeroSubmitted;
    private int measurementHeroReady;
    private int measurementHeroValid;
    private int measurementInstrumentationReadbackBytes;
    private ulong observedTimestampFrequency;
    private uint observedDeviceGeneration;
    private double lastMeasurementHeroMilliseconds;

    private GpuCinematicNativeTimestampBackend(
        GpuPrimitiveNativeTimestampBackend backend,
        GpuTimestampSupport support)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Support = support;
        if (backend.Capacity <= 0)
        {
            throw new InvalidOperationException(
                "The native timestamp backend exposed no usable scopes.");
        }

        beginCommands = new CommandBuffer[backend.Capacity];
        endCommands = new CommandBuffer[backend.Capacity];
        try
        {
            int initialCount = Math.Min(
                InitialPreparedScopes,
                backend.Capacity);
            for (int scopeIndex = 0;
                scopeIndex < initialCount;
                scopeIndex++)
            {
                EnsureScopeCommands(scopeIndex);
            }
            backend.ExecuteFrequencyInitialization();
        }
        catch
        {
            DisposeCommands();
            throw;
        }
    }

    public GpuTimestampSupport Support { get; }

    public IReadOnlyList<GpuCinematicTimestampEvidence> Evidence => evidence;

    public bool Operational => operational && !backend.IsTerminal;

    public bool IsTerminal => backend.IsTerminal;

    public string TerminalStatus => backend.TerminalStatus.ToString();

    public bool HasActiveHero => activeHeroBegun;

    public int PendingCount => pending.Count;

    public int ActiveSampleCount => backend.ActiveSampleCount;

    public int ReservedSampleCount => backend.ReservedSampleCount;

    public int SubmittedSampleCount => backend.SubmittedSampleCount;

    public int PreparedScopeCount => preparedScopeCount;

    public int PeakActiveSamples => peakActiveSamples;

    public int AcquireFailures => acquireFailures;

    public int PreparedScopeFailures => preparedScopeFailures;

    public int ResultFailures => resultFailures;

    public int Timeouts => timeouts;

    public int WarmupControlSubmitted => warmupControlSubmitted;

    public int WarmupControlReady => warmupControlReady;

    public int WarmupControlValid => warmupControlValid;

    public int WarmupHeroSubmitted => warmupHeroSubmitted;

    public int WarmupHeroReady => warmupHeroReady;

    public int WarmupHeroValid => warmupHeroValid;

    public int MeasurementControlSubmitted => measurementControlSubmitted;

    public int MeasurementControlReady => measurementControlReady;

    public int MeasurementControlValid => measurementControlValid;

    public int MeasurementHeroSubmitted => measurementHeroSubmitted;

    public int MeasurementHeroReady => measurementHeroReady;

    public int MeasurementHeroValid => measurementHeroValid;

    public int MeasurementInstrumentationReadbackBytes =>
        measurementInstrumentationReadbackBytes;

    public ulong ObservedTimestampFrequency => observedTimestampFrequency;

    public uint ObservedDeviceGeneration => observedDeviceGeneration;

    public bool OrderingDiscriminatorPassed => orderingDiscriminatorPassed;

    public double LastMeasurementHeroMilliseconds =>
        lastMeasurementHeroMilliseconds;

    public static bool TryCreate(
        out GpuCinematicNativeTimestampBackend result,
        out GpuTimestampSupport support,
        out string error)
    {
        result = null;
        support = default;
        error = string.Empty;
        GpuPrimitiveNativeTimestampBackend backend = null;
        try
        {
            if (!GpuPrimitiveNativeTimestampBackend.TryCreate(
                    out backend,
                    out support))
            {
                error = support.Message;
                return false;
            }
            if (support.AbiVersion != 2 ||
                (support.CapabilityFlags & RequiredCapabilities) !=
                    RequiredCapabilities)
            {
                error = "Native timestamp ABI/capability mismatch: abi=" +
                    support.AbiVersion + ", capabilities=0x" +
                    support.CapabilityFlags.ToString("X", Invariant) + ".";
                backend.Dispose();
                backend = null;
                return false;
            }
            result = new GpuCinematicNativeTimestampBackend(backend, support);
            backend = null;
            return true;
        }
        catch (Exception exception)
        {
            backend?.Dispose();
            error = exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    public bool BeginHero(
        int sampleIndex,
        GpuCinematicTimestampKind kind,
        out string error)
    {
        ThrowIfDisposed();
        error = string.Empty;
        if (!IsHeroKind(kind) || activeHeroBegun || !Operational)
        {
            error = "A native hero scope is already active or unavailable.";
            return false;
        }

        if (!TryReserve(
                sampleIndex,
                kind,
                out GpuTimestampToken token,
                out int evidenceIndex,
                out error) ||
            !TryMarkSubmitted(token, evidenceIndex, kind, out error))
        {
            return false;
        }

        activeHeroToken = token;
        activeHeroKind = kind;
        activeHeroSampleIndex = sampleIndex;
        activeHeroEvidenceIndex = evidenceIndex;
        activeHeroSrpCallbacks = 0;
        activeHeroBegun = true;
        IncrementSubmitted(kind);
        try
        {
            Graphics.ExecuteCommandBuffer(beginCommands[token.ScopeIndex]);
            return true;
        }
        catch (Exception exception)
        {
            error = "Native begin submission failed: " + exception.Message;
            resultFailures++;
            operational = false;
            ClearActiveHero();
            return false;
        }
    }

    public bool SubmitEmptyControl(
        int sampleIndex,
        GpuCinematicTimestampKind kind,
        out string error)
    {
        ThrowIfDisposed();
        error = string.Empty;
        if (!IsControlKind(kind) || !Operational)
        {
            error = "The native empty-control scope is unavailable.";
            return false;
        }

        if (!TryReserve(
                sampleIndex,
                kind,
                out GpuTimestampToken token,
                out int evidenceIndex,
                out error) ||
            !TryMarkSubmitted(token, evidenceIndex, kind, out error))
        {
            return false;
        }

        IncrementSubmitted(kind);
        try
        {
            Graphics.ExecuteCommandBuffer(beginCommands[token.ScopeIndex]);
            Graphics.ExecuteCommandBuffer(endCommands[token.ScopeIndex]);
            AddPending(
                token,
                kind,
                sampleIndex,
                evidenceIndex,
                srpHeroCallbacks: 0);
            return true;
        }
        catch (Exception exception)
        {
            error = "Native empty-control submission failed: " +
                exception.Message;
            resultFailures++;
            operational = false;
            return false;
        }
    }

    public void NoteHeroSrpCallback()
    {
        if (activeHeroBegun)
        {
            activeHeroSrpCallbacks++;
        }
    }

    public bool EndHero(out string error)
    {
        ThrowIfDisposed();
        error = string.Empty;
        if (!activeHeroBegun)
        {
            return true;
        }

        GpuTimestampToken token = activeHeroToken;
        GpuCinematicTimestampKind kind = activeHeroKind;
        int sampleIndex = activeHeroSampleIndex;
        int evidenceIndex = activeHeroEvidenceIndex;
        int srpCallbacks = activeHeroSrpCallbacks;
        try
        {
            Graphics.ExecuteCommandBuffer(endCommands[token.ScopeIndex]);
            AddPending(
                token,
                kind,
                sampleIndex,
                evidenceIndex,
                srpCallbacks);
            ClearActiveHero();
            return true;
        }
        catch (Exception exception)
        {
            error = "Native end submission failed: " + exception.Message;
            resultFailures++;
            operational = false;
            return false;
        }
    }

    public void Poll(int resultFrame, IList<float> measurementGpuSamples)
    {
        ThrowIfDisposed();
        for (int index = pending.Count - 1; index >= 0; index--)
        {
            PendingSample sample = pending[index];
            GpuTimestampStatus status = backend.TryConsume(
                sample.Token,
                resultFrame,
                out GpuTimestampResult result);
            if (status == GpuTimestampStatus.Pending)
            {
                continue;
            }

            GpuCinematicTimestampEvidence row = evidence[sample.EvidenceIndex];
            row.ResultFrame = resultFrame;
            row.PendingFrames = resultFrame - row.SourceFrame;
            row.SrpHeroCallbacks = sample.SrpHeroCallbacks;
            row.Status = status.ToString();
            if (status == GpuTimestampStatus.Ready)
            {
                IncrementReady(sample.Kind);
                PopulateReadyEvidence(ref row, result);
                row.Valid = ValidateReadyResult(sample, row, result);
                if (row.Valid)
                {
                    if (sample.Kind ==
                        GpuCinematicTimestampKind.MeasurementHero)
                    {
                        if (measurementGpuSamples == null ||
                            sample.SampleIndex < 0 ||
                            sample.SampleIndex >= measurementGpuSamples.Count ||
                            measurementGpuSamples[sample.SampleIndex] > 0.0f)
                        {
                            row.Valid = false;
                        }
                        else
                        {
                            float elapsed = (float)row.ElapsedMilliseconds;
                            measurementGpuSamples[sample.SampleIndex] = elapsed;
                            lastMeasurementHeroMilliseconds = elapsed;
                        }
                    }
                    if (row.Valid)
                    {
                        IncrementValid(sample.Kind);
                    }
                }
                if (!row.Valid)
                {
                    resultFailures++;
                    operational = false;
                }
                if (IsMeasurementKind(sample.Kind))
                {
                    measurementInstrumentationReadbackBytes += 16;
                }
            }
            else
            {
                row.Valid = false;
                resultFailures++;
                operational = false;
            }
            evidence[sample.EvidenceIndex] = row;
            pending.RemoveAt(index);
        }
    }

    public bool ValidateWarmup(int expectedPairs, out string error)
    {
        ThrowIfDisposed();
        double controlP99 = Percentile(
            GpuCinematicTimestampKind.WarmupControl,
            0.99);
        double heroP50 = Percentile(
            GpuCinematicTimestampKind.WarmupHero,
            0.50);
        orderingDiscriminatorPassed = heroP50 > controlP99;
        bool valid = expectedPairs > 0 &&
            warmupControlSubmitted == expectedPairs &&
            warmupControlReady == expectedPairs &&
            warmupControlValid == expectedPairs &&
            warmupHeroSubmitted == expectedPairs &&
            warmupHeroReady == expectedPairs &&
            warmupHeroValid == expectedPairs &&
            orderingDiscriminatorPassed &&
            pending.Count == 0 &&
            !activeHeroBegun &&
            BackendCountsAreClear() &&
            acquireFailures == 0 &&
            preparedScopeFailures == 0 &&
            resultFailures == 0 &&
            timeouts == 0 &&
            observedTimestampFrequency > 0 &&
            observedDeviceGeneration == Support.DeviceGeneration &&
            Operational;
        error = valid
            ? string.Empty
            : "control=" + warmupControlValid + "/" + expectedPairs +
              "; hero=" + warmupHeroValid + "/" + expectedPairs +
              "; heroP50=" + heroP50.ToString("F6", Invariant) +
              "ms; controlP99=" + controlP99.ToString("F6", Invariant) +
              "ms; failures=" + acquireFailures + "/" +
              preparedScopeFailures + "/" + resultFailures + "/" +
              timeouts + "; pending=" + pending.Count + ".";
        return valid;
    }

    public bool ResetMeasurement(out string error)
    {
        ThrowIfDisposed();
        if (pending.Count != 0 || activeHeroBegun ||
            !BackendCountsAreClear() || !Operational)
        {
            error = "The native timestamp backend was not idle at the " +
                "measurement boundary.";
            return false;
        }
        measurementControlSubmitted = 0;
        measurementControlReady = 0;
        measurementControlValid = 0;
        measurementHeroSubmitted = 0;
        measurementHeroReady = 0;
        measurementHeroValid = 0;
        measurementInstrumentationReadbackBytes = 0;
        lastMeasurementHeroMilliseconds = 0.0;
        error = string.Empty;
        return true;
    }

    public bool ValidateMeasurement(
        int expectedHeroSamples,
        int expectedControlSamples,
        IList<float> measurementGpuSamples,
        out string error)
    {
        ThrowIfDisposed();
        bool samplesValid = measurementGpuSamples != null &&
            measurementGpuSamples.Count == expectedHeroSamples;
        if (samplesValid)
        {
            for (int i = 0; i < measurementGpuSamples.Count; i++)
            {
                if (!(measurementGpuSamples[i] > 0.0f))
                {
                    samplesValid = false;
                    break;
                }
            }
        }

        bool identitiesUnique = ValidateUniqueIdentities();
        bool valid = expectedHeroSamples > 0 &&
            expectedControlSamples > 0 &&
            measurementHeroSubmitted == expectedHeroSamples &&
            measurementHeroReady == expectedHeroSamples &&
            measurementHeroValid == expectedHeroSamples &&
            measurementControlSubmitted == expectedControlSamples &&
            measurementControlReady == expectedControlSamples &&
            measurementControlValid == expectedControlSamples &&
            measurementInstrumentationReadbackBytes ==
                checked((expectedHeroSamples + expectedControlSamples) * 16) &&
            samplesValid &&
            identitiesUnique &&
            orderingDiscriminatorPassed &&
            pending.Count == 0 &&
            !activeHeroBegun &&
            BackendCountsAreClear() &&
            acquireFailures == 0 &&
            preparedScopeFailures == 0 &&
            resultFailures == 0 &&
            timeouts == 0 &&
            observedTimestampFrequency > 0 &&
            observedDeviceGeneration == Support.DeviceGeneration &&
            Operational;
        error = valid
            ? string.Empty
            : "hero=" + measurementHeroValid + "/" +
              expectedHeroSamples + "; control=" +
              measurementControlValid + "/" + expectedControlSamples +
              "; failures=" + acquireFailures + "/" +
              preparedScopeFailures + "/" + resultFailures + "/" +
              timeouts + "; pending=" + pending.Count +
              "; backend=" + ActiveSampleCount + "/" +
              ReservedSampleCount + "/" + SubmittedSampleCount + ".";
        return valid;
    }

    public void MarkPendingTimeouts()
    {
        ThrowIfDisposed();
        if (pending.Count == 0)
        {
            return;
        }
        int newTimeouts = 0;
        for (int i = 0; i < pending.Count; i++)
        {
            PendingSample sample = pending[i];
            GpuCinematicTimestampEvidence row = evidence[sample.EvidenceIndex];
            if (string.Equals(
                    row.Status,
                    "timeout",
                    StringComparison.Ordinal))
            {
                continue;
            }
            row.Status = "timeout";
            row.Valid = false;
            row.SrpHeroCallbacks = sample.SrpHeroCallbacks;
            evidence[sample.EvidenceIndex] = row;
            newTimeouts++;
        }
        timeouts += newTimeouts;
        operational = false;
    }

    public double Average(GpuCinematicTimestampKind kind)
    {
        double sum = 0.0;
        int count = 0;
        for (int i = 0; i < evidence.Count; i++)
        {
            GpuCinematicTimestampEvidence row = evidence[i];
            if (row.Kind == kind && row.Valid)
            {
                sum += row.ElapsedMilliseconds;
                count++;
            }
        }
        return count > 0 ? sum / count : 0.0;
    }

    public double Percentile(
        GpuCinematicTimestampKind kind,
        double percentile)
    {
        var values = new List<double>();
        for (int i = 0; i < evidence.Count; i++)
        {
            GpuCinematicTimestampEvidence row = evidence[i];
            if (row.Kind == kind && row.Valid)
            {
                values.Add(row.ElapsedMilliseconds);
            }
        }
        if (values.Count == 0)
        {
            return 0.0;
        }
        values.Sort();
        double position = Math.Max(0.0, Math.Min(1.0, percentile)) *
            (values.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return values[lower];
        }
        double weight = position - lower;
        return values[lower] * (1.0 - weight) + values[upper] * weight;
    }

    public int MaximumPendingFrames(GpuCinematicTimestampKind kind)
    {
        int maximum = 0;
        for (int i = 0; i < evidence.Count; i++)
        {
            GpuCinematicTimestampEvidence row = evidence[i];
            if (row.Kind == kind && row.Valid)
            {
                maximum = Math.Max(maximum, row.PendingFrames);
            }
        }
        return maximum;
    }

    public string BuildEvidenceCsv()
    {
        var csv = new StringBuilder(evidence.Count * 160);
        csv.AppendLine(
            "kind,sampleIndex,sourceFrame,resultFrame,pendingFrames," +
            "token,userTag,flags,status,beginTicks,endTicks,elapsedTicks," +
            "frequency,elapsedNanoseconds,elapsedMs,fenceValue," +
            "deviceGeneration,srpHeroCallbacks,valid");
        for (int i = 0; i < evidence.Count; i++)
        {
            GpuCinematicTimestampEvidence row = evidence[i];
            csv.Append(row.Kind).Append(',')
                .Append(row.SampleIndex).Append(',')
                .Append(row.SourceFrame).Append(',')
                .Append(row.ResultFrame).Append(',')
                .Append(row.PendingFrames).Append(',')
                .Append(row.Token).Append(',')
                .Append(row.UserTag).Append(',')
                .Append(row.Flags).Append(',')
                .Append(row.Status ?? string.Empty).Append(',')
                .Append(row.BeginTicks).Append(',')
                .Append(row.EndTicks).Append(',')
                .Append(row.ElapsedTicks).Append(',')
                .Append(row.TimestampFrequency).Append(',')
                .Append(row.ElapsedNanoseconds).Append(',')
                .Append(row.ElapsedMilliseconds.ToString("F9", Invariant))
                .Append(',').Append(row.FenceValue).Append(',')
                .Append(row.DeviceGeneration).Append(',')
                .Append(row.SrpHeroCallbacks).Append(',')
                .Append(row.Valid ? 1 : 0).AppendLine();
        }
        return csv.ToString();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        DisposeCommands();
        backend.Dispose();
        pending.Clear();
        ClearActiveHero();
    }

    private bool TryReserve(
        int sampleIndex,
        GpuCinematicTimestampKind kind,
        out GpuTimestampToken token,
        out int evidenceIndex,
        out string error)
    {
        token = default;
        evidenceIndex = -1;
        error = string.Empty;
        GpuTimestampSampleFlags flags = IsControlKind(kind)
            ? GpuTimestampSampleFlags.EmptyScope
            : GpuTimestampSampleFlags.None;
        ulong userTag = nextUserTag++;
        if (userTag == 0UL)
        {
            acquireFailures++;
            operational = false;
            error = "The native timestamp user-tag sequence overflowed.";
            return false;
        }

        GpuTimestampStatus status = backend.Acquire(
            userTag,
            flags,
            Time.frameCount,
            out token);
        if (status != GpuTimestampStatus.Ready)
        {
            acquireFailures++;
            error = "Native timestamp acquire returned " + status + ".";
            return false;
        }
        if (token.ScopeIndex < 0 ||
            token.ScopeIndex >= beginCommands.Length)
        {
            preparedScopeFailures++;
            backend.Cancel(token);
            error = "The acquired timestamp scope was outside capacity.";
            return false;
        }
        try
        {
            EnsureScopeCommands(token.ScopeIndex);
        }
        catch (Exception exception)
        {
            preparedScopeFailures++;
            backend.Cancel(token);
            error = "Could not prepare timestamp scope " +
                token.ScopeIndex + ": " + exception.Message;
            return false;
        }

        evidenceIndex = evidence.Count;
        evidence.Add(new GpuCinematicTimestampEvidence
        {
            Kind = kind,
            SampleIndex = sampleIndex,
            Flags = (uint)flags,
            Status = "reserved",
            Token = token.Value,
            UserTag = token.UserTag,
            SourceFrame = token.SourceFrame
        });
        return true;
    }

    private bool TryMarkSubmitted(
        GpuTimestampToken token,
        int evidenceIndex,
        GpuCinematicTimestampKind kind,
        out string error)
    {
        GpuTimestampStatus status = backend.MarkSubmitted(token);
        if (status != GpuTimestampStatus.Ready)
        {
            resultFailures++;
            GpuCinematicTimestampEvidence failed = evidence[evidenceIndex];
            failed.Status = "mark-" + status;
            evidence[evidenceIndex] = failed;
            operational = false;
            error = "Native timestamp MarkSubmitted returned " + status + ".";
            return false;
        }
        GpuCinematicTimestampEvidence row = evidence[evidenceIndex];
        row.Status = "submitted";
        evidence[evidenceIndex] = row;
        UpdatePeakActiveSamples();
        error = string.Empty;
        return true;
    }

    private void AddPending(
        GpuTimestampToken token,
        GpuCinematicTimestampKind kind,
        int sampleIndex,
        int evidenceIndex,
        int srpHeroCallbacks)
    {
        pending.Add(new PendingSample
        {
            Token = token,
            Kind = kind,
            SampleIndex = sampleIndex,
            EvidenceIndex = evidenceIndex,
            SrpHeroCallbacks = srpHeroCallbacks
        });
        GpuCinematicTimestampEvidence row = evidence[evidenceIndex];
        row.Status = "pending";
        row.SrpHeroCallbacks = srpHeroCallbacks;
        evidence[evidenceIndex] = row;
        UpdatePeakActiveSamples();
    }

    private bool ValidateReadyResult(
        PendingSample sample,
        GpuCinematicTimestampEvidence row,
        GpuTimestampResult result)
    {
        bool expectsControl = IsControlKind(sample.Kind);
        uint expectedFlags = expectsControl
            ? (uint)GpuTimestampSampleFlags.EmptyScope
            : (uint)GpuTimestampSampleFlags.None;
        bool frequencyValid = result.TimestampFrequency > 0 &&
            (observedTimestampFrequency == 0 ||
             observedTimestampFrequency == result.TimestampFrequency);
        bool generationValid =
            result.DeviceGeneration == Support.DeviceGeneration &&
            (observedDeviceGeneration == 0 ||
             observedDeviceGeneration == result.DeviceGeneration);
        bool durationValid = expectsControl
            ? result.ElapsedMilliseconds >= 0.0
            : result.ElapsedTicks > 0 && result.ElapsedMilliseconds > 0.0;
        bool valid =
            result.Token.Value == sample.Token.Value &&
            result.Token.UserTag == sample.Token.UserTag &&
            result.Token.Flags == sample.Token.Flags &&
            result.SourceFrame == sample.Token.SourceFrame &&
            result.ResultFrame == row.ResultFrame &&
            result.NativeFlags == expectedFlags &&
            result.FenceValue > 0 &&
            result.EndTicks >= result.BeginTicks &&
            result.ElapsedTicks == result.EndTicks - result.BeginTicks &&
            result.ElapsedNanoseconds >= 0 &&
            frequencyValid &&
            generationValid &&
            durationValid &&
            row.PendingFrames >= 0 &&
            sample.SrpHeroCallbacks == (expectsControl ? 0 : 1);
        if (valid)
        {
            if (observedTimestampFrequency == 0)
            {
                observedTimestampFrequency = result.TimestampFrequency;
            }
            if (observedDeviceGeneration == 0)
            {
                observedDeviceGeneration = result.DeviceGeneration;
            }
        }
        return valid;
    }

    private static void PopulateReadyEvidence(
        ref GpuCinematicTimestampEvidence row,
        GpuTimestampResult result)
    {
        row.Token = result.Token.Value;
        row.UserTag = result.Token.UserTag;
        row.Flags = result.NativeFlags;
        row.BeginTicks = result.BeginTicks;
        row.EndTicks = result.EndTicks;
        row.ElapsedTicks = result.ElapsedTicks;
        row.TimestampFrequency = result.TimestampFrequency;
        row.ElapsedNanoseconds = result.ElapsedNanoseconds;
        row.ElapsedMilliseconds = result.ElapsedMilliseconds;
        row.FenceValue = result.FenceValue;
        row.DeviceGeneration = result.DeviceGeneration;
    }

    private bool ValidateUniqueIdentities()
    {
        var tokens = new HashSet<ulong>();
        var tags = new HashSet<ulong>();
        for (int i = 0; i < evidence.Count; i++)
        {
            GpuCinematicTimestampEvidence row = evidence[i];
            if (!row.Valid || row.Token == 0UL || row.UserTag == 0UL ||
                !tokens.Add(row.Token) || !tags.Add(row.UserTag))
            {
                return false;
            }
        }
        return evidence.Count > 0;
    }

    private bool BackendCountsAreClear()
    {
        return backend.ActiveSampleCount == 0 &&
            backend.ReservedSampleCount == 0 &&
            backend.SubmittedSampleCount == 0;
    }

    private void IncrementSubmitted(GpuCinematicTimestampKind kind)
    {
        switch (kind)
        {
            case GpuCinematicTimestampKind.WarmupControl:
                warmupControlSubmitted++;
                break;
            case GpuCinematicTimestampKind.WarmupHero:
                warmupHeroSubmitted++;
                break;
            case GpuCinematicTimestampKind.MeasurementControl:
                measurementControlSubmitted++;
                break;
            case GpuCinematicTimestampKind.MeasurementHero:
                measurementHeroSubmitted++;
                break;
        }
    }

    private void IncrementReady(GpuCinematicTimestampKind kind)
    {
        switch (kind)
        {
            case GpuCinematicTimestampKind.WarmupControl:
                warmupControlReady++;
                break;
            case GpuCinematicTimestampKind.WarmupHero:
                warmupHeroReady++;
                break;
            case GpuCinematicTimestampKind.MeasurementControl:
                measurementControlReady++;
                break;
            case GpuCinematicTimestampKind.MeasurementHero:
                measurementHeroReady++;
                break;
        }
    }

    private void IncrementValid(GpuCinematicTimestampKind kind)
    {
        switch (kind)
        {
            case GpuCinematicTimestampKind.WarmupControl:
                warmupControlValid++;
                break;
            case GpuCinematicTimestampKind.WarmupHero:
                warmupHeroValid++;
                break;
            case GpuCinematicTimestampKind.MeasurementControl:
                measurementControlValid++;
                break;
            case GpuCinematicTimestampKind.MeasurementHero:
                measurementHeroValid++;
                break;
        }
    }

    private void UpdatePeakActiveSamples()
    {
        peakActiveSamples = Math.Max(
            peakActiveSamples,
            backend.ActiveSampleCount);
    }

    private void ClearActiveHero()
    {
        activeHeroToken = default;
        activeHeroKind = default;
        activeHeroSampleIndex = -1;
        activeHeroEvidenceIndex = -1;
        activeHeroSrpCallbacks = 0;
        activeHeroBegun = false;
    }

    private static bool IsHeroKind(GpuCinematicTimestampKind kind)
    {
        return kind == GpuCinematicTimestampKind.WarmupHero ||
            kind == GpuCinematicTimestampKind.MeasurementHero;
    }

    private static bool IsControlKind(GpuCinematicTimestampKind kind)
    {
        return kind == GpuCinematicTimestampKind.WarmupControl ||
            kind == GpuCinematicTimestampKind.MeasurementControl;
    }

    private static bool IsMeasurementKind(GpuCinematicTimestampKind kind)
    {
        return kind == GpuCinematicTimestampKind.MeasurementControl ||
            kind == GpuCinematicTimestampKind.MeasurementHero;
    }

    private void EnsureScopeCommands(int scopeIndex)
    {
        if (beginCommands[scopeIndex] != null &&
            endCommands[scopeIndex] != null)
        {
            return;
        }
        var begin = new CommandBuffer
        {
            name = "GPU.Cinematic/NativeTimestamp/Begin/" + scopeIndex
        };
        var end = new CommandBuffer
        {
            name = "GPU.Cinematic/NativeTimestamp/End/" + scopeIndex
        };
        try
        {
            backend.RecordBegin(scopeIndex, begin);
            backend.RecordEnd(scopeIndex, end);
            beginCommands[scopeIndex] = begin;
            endCommands[scopeIndex] = end;
            preparedScopeCount++;
        }
        catch
        {
            begin.Dispose();
            end.Dispose();
            throw;
        }
    }

    private void DisposeCommands()
    {
        if (beginCommands != null)
        {
            for (int i = 0; i < beginCommands.Length; i++)
            {
                beginCommands[i]?.Dispose();
            }
        }
        if (endCommands != null)
        {
            for (int i = 0; i < endCommands.Length; i++)
            {
                endCommands[i]?.Dispose();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuCinematicNativeTimestampBackend));
        }
    }
}
