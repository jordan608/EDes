// Optional: run the same code against a REAL instrument.
//     dotnet run --project tests/ScpiTests <scope-ip>
// Skipped when no host is given, so CI still passes with no bench attached.

using EDes.Sim.Scpi;

namespace ScpiTests;

public static class RealScope
{
    public static int Run(string host, int port = 5555)
    {
        int failures = 0;
        void CheckTrue(string what, bool ok)
        {
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        }

        Console.WriteLine($"connecting to {host}:{port} ...");
        try
        {
            using var transport = new TcpScpiTransport(host, port, 4000);
            var scope = new ScpiScope(transport);
            scope.Open();

            Console.WriteLine("  IDN: " + scope.Identity);
            Console.WriteLine($"  inferred analog channels: {scope.AnalogChannels}");
            CheckTrue("*IDN? answered", scope.Identity.Length > 0);

            uint mask = scope.QueryEnabledChannels();
            Console.WriteLine($"  enabled channel mask: 0b{Convert.ToString(mask, 2).PadLeft(4, '0')}");
            CheckTrue("at least one channel is on", mask != 0);

            for (int ch = 1; ch <= 4; ch++)
            {
                if ((mask & (1u << (ch - 1))) == 0) continue;

                var wf = scope.ReadChannel(ch);
                CheckTrue($"CH{ch} waveform read", wf != null && wf.Count > 100);
                if (wf == null) continue;

                float min = float.MaxValue, max = float.MinValue, sum = 0;
                for (int i = 0; i < wf.Count; i++)
                {
                    float v = wf.Volts[i];
                    if (v < min) min = v;
                    if (v > max) max = v;
                    sum += v;
                }

                Console.WriteLine($"  CH{ch}: {wf.Count} pts @ {wf.SampleRateHz / 1e6:0.###} MSa/s   " +
                                  $"min {min:0.###} V  max {max:0.###} V  mean {sum / wf.Count:0.###} V");

                CheckTrue($"CH{ch} sample rate is plausible (1 kSa/s .. 5 GSa/s)",
                          wf.SampleRateHz is > 1_000 and < 5e9);
                CheckTrue($"CH{ch} volts are in a sane instrument range (+/-100 V)",
                          min > -100 && max < 100);
                CheckTrue($"CH{ch} returned the full screen record (>=1000 pts)", wf.Count >= 1000);
            }

            // Two reads back to back on one connection: modal state must survive.
            var again = scope.ReadChannel(1);
            CheckTrue("second read on the same connection", again != null && again.Count > 100);
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"FAIL  {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine(failures == 0 ? "REAL SCOPE CHECKS PASSED" : $"{failures} REAL SCOPE CHECK(S) FAILED");
        return failures;
    }
}
