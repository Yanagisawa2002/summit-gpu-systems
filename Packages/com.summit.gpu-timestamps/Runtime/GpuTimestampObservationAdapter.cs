using System;

namespace Summit.GpuTimestamps
{
    public static class GpuTimestampObservationAdapter
    {
        // Explicit native scopes never populate full-engine GPU or presentation fields.
        public static Observation Convert(ObservationSource source, GpuTimestampResult result)
        {
            bool valid = source.DeviceGeneration == result.DeviceGeneration && result.TimestampFrequency > 0 &&
                result.EndTicks >= result.BeginTicks && result.SourceFrame >= 0;
            return new Observation(source, result.Token.Value.ToString(), ObservationScope.ExplicitGpuWork,
                ObservationUnit.Milliseconds, valid ? ObservationState.Available : ObservationState.Invalid,
                valid ? (double?)(1000.0 * (result.EndTicks - result.BeginTicks) / result.TimestampFrequency) : null,
                valid ? "" : "generation-or-timestamp-invalid", result.SourceFrame, result.ResultFrame,
                valid ? result.BeginTicks : 0, valid ? result.EndTicks : 0, valid ? result.TimestampFrequency : 0);
        }
    }
}
