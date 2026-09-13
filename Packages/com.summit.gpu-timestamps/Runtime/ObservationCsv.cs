using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Summit.GpuTimestamps
{
    /// <summary>Lossless correlation sidecar for ETW/PresentMon/PIX exports. This is
    /// a versioned interchange CSV, not a parser for proprietary .etl/.pix3 files.</summary>
    public static class ObservationCsv
    {
        public const string Header = "schema,sourceCommit,buildId,deviceDriverId,collector,clockDomain,queue,processId,deviceGeneration,eventId,scope,unit,state,value,reason,sourceFrame,observedFrame,startTicks,endTicks,frequency";
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        public static void Write(TextWriter writer, IEnumerable<Observation> rows)
        {
            writer.WriteLine(Header);
            foreach (var r in rows)
            {
                var s = r.Source;
                string[] fields = { "summit-observation-v1", s.SourceCommit, s.BuildId, s.DeviceDriverId,
                    s.Collector, s.ClockDomain, s.Queue, s.ProcessId.ToString(Culture), s.DeviceGeneration.ToString(Culture),
                    r.EventId, r.Scope.ToString(), r.Unit.ToString(), r.State.ToString(), r.Value?.ToString("R", Culture) ?? "",
                    r.Reason, r.SourceFrame.ToString(Culture), r.ObservedFrame.ToString(Culture),
                    r.StartTicks.ToString(Culture), r.EndTicks.ToString(Culture), r.Frequency.ToString(Culture) };
                writer.WriteLine(string.Join(",", Array.ConvertAll(fields, Escape)));
            }
        }
        static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        public static IEnumerable<Observation> Read(TextReader reader)
        {
            if (reader.ReadLine() != Header) throw new FormatException("Unknown observation CSV header/schema.");
            foreach (string[] f in Records(reader))
            {
                if (f.Length != 20 || f[0] != "summit-observation-v1") throw new FormatException("Invalid observation CSV row.");
                var source = new ObservationSource(f[1], f[2], f[3], f[4], f[5], f[6], int.Parse(f[7], Culture), uint.Parse(f[8], Culture));
                yield return new Observation(source, f[9], Parse<ObservationScope>(f[10]), Parse<ObservationUnit>(f[11]),
                    Parse<ObservationState>(f[12]), f[13] == "" ? (double?)null : double.Parse(f[13], Culture), f[14],
                    int.Parse(f[15], Culture), int.Parse(f[16], Culture), ulong.Parse(f[17], Culture),
                    ulong.Parse(f[18], Culture), ulong.Parse(f[19], Culture));
            }
        }
        static T Parse<T>(string value) where T : struct => Enum.TryParse(value, out T result) && Enum.IsDefined(typeof(T), result)
            ? result : throw new FormatException("Invalid observation enum.");

        static IEnumerable<string[]> Records(TextReader reader)
        {
            var fields = new List<string>(); var field = new StringBuilder();
            bool quoted = false, closed = false, started = false;
            for (int c; (c = reader.Read()) >= 0;)
            {
                char ch = (char)c; started = true;
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                        else { quoted = false; closed = true; }
                    }
                    else field.Append(ch);
                }
                else if (ch == ',' || ch == '\r' || ch == '\n')
                {
                    fields.Add(field.ToString()); field.Clear(); closed = false;
                    if (ch != ',')
                    {
                        if (ch == '\r' && reader.Peek() == '\n') reader.Read();
                        yield return fields.ToArray(); fields.Clear(); started = false;
                    }
                }
                else if (ch == '"' && field.Length == 0 && !closed) quoted = true;
                else if (closed || ch == '"') throw new FormatException("Malformed CSV quoting.");
                else field.Append(ch);
            }
            if (quoted) throw new FormatException("Unterminated CSV quote.");
            if (started) { fields.Add(field.ToString()); yield return fields.ToArray(); }
        }
    }
}
