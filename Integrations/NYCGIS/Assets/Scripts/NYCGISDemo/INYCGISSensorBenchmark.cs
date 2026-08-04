using System;
using System.Text;

public interface INYCGISSensorBenchmark : IDisposable
{
    bool HasPendingReadbacks { get; }
    bool Passed { get; }
    void BeginWarmup(double now);
    void BeginMeasurement(double now);
    void Tick(double now);
    void StopIssuing();
    void PollOnly(double now);
    void AppendReport(StringBuilder report, double measurementElapsedSeconds);
}
