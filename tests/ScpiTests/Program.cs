// SCPI transport + waveform checks against a FAKE instrument.
//
// A TcpListener on localhost speaks the Rigol DS2000/MSO2000 subset EDes uses
// (*IDN?, :CHANn:DISP?, :WAV:PRE?, :WAV:DATA? with an IEEE-488.2 definite-length
// block). That exercises the real transport and the real conversion maths end to
// end — everything except the physical link — so a block-header or scaling bug is
// caught here rather than on the bench.
//
//     dotnet run --project tests/ScpiTests        (exit 0 = all checks passed)

using System.Net;
using System.Net.Sockets;
using System.Text;
using EDes.Sim.Scpi;

int failures = 0;

void Check(string what, double actual, double expected, double tol = 1e-6)
{
    bool ok = Math.Abs(actual - expected) <= tol;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}: got {actual:0.######}, expected {expected:0.######}");
}

void CheckTrue(string what, bool ok)
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
}

// ── Block header parsing (pure, no socket) ───────────────────────────────────
Console.WriteLine("=== IEEE-488.2 block headers ===");
{
    byte[] b = Encoding.ASCII.GetBytes("#9000001400" + new string('x', 10));
    int header = ScpiBlock.HeaderLength(b, b.Length, out int payload);
    Check("header length", header, 11);
    Check("declared payload", payload, 1400);

    b = Encoding.ASCII.GetBytes("#42048abcd");
    header = ScpiBlock.HeaderLength(b, b.Length, out payload);
    Check("short-form header length", header, 6);
    Check("short-form payload", payload, 2048);

    b = Encoding.ASCII.GetBytes("\n#800001024rest");
    header = ScpiBlock.HeaderLength(b, b.Length, out payload);
    Check("leading newline skipped", header, 11);
    Check("payload after newline", payload, 1024);

    b = Encoding.ASCII.GetBytes("1.234\n");
    header = ScpiBlock.HeaderLength(b, b.Length, out payload);
    Check("plain ASCII reply is not a block", header, 0);
}

// ── Preamble parsing + volts conversion ──────────────────────────────────────
Console.WriteLine();
Console.WriteLine("=== :WAV:PRE? parsing and scaling ===");
{
    // A realistic MSO2302A preamble: 1400 points, 500 ns/sample, 8 mV/LSB,
    // yorigin -100, yreference 127.
    string reply = "0,0,1400,1,5.000000e-07,-3.500000e-04,700,8.000000e-03,-100,127";
    bool ok = ScpiScope.TryParsePreamble(reply, out var pre);
    CheckTrue("preamble parsed", ok);
    Check("points", pre.Points, 1400);
    Check("xincrement", pre.XIncrement, 5e-7, 1e-12);
    Check("sample rate from xincrement (Hz)", 1.0 / pre.XIncrement, 2_000_000, 1);
    Check("yincrement", pre.YIncrement, 0.008, 1e-9);
    Check("yorigin", pre.YOrigin, -100);
    Check("yreference", pre.YReference, 127);

    // volts = (raw - yorigin - yreference) * yincrement
    Check("raw 27 -> volts",  ScpiScope.RawToVolts(27, pre),  0.0, 1e-6);
    Check("raw 127 -> volts", ScpiScope.RawToVolts(127, pre), 0.8, 1e-6);
    Check("raw 0 -> volts",   ScpiScope.RawToVolts(0, pre),  -0.216, 1e-6);
    Check("raw 255 -> volts", ScpiScope.RawToVolts(255, pre), 1.824, 1e-6);

    Check("MSO2302A -> 2 analog channels",
          ScpiScope.InferAnalogChannels("RIGOL TECHNOLOGIES,MSO2302A,DS2Axxxxxxxx,00.03.06"), 2);
    Check("DS1054Z -> 4 analog channels",
          ScpiScope.InferAnalogChannels("RIGOL TECHNOLOGIES,DS1054Z,DS1ZA1,00.04.04"), 4);
    Check("DS2072A -> 2 analog channels",
          ScpiScope.InferAnalogChannels("RIGOL TECHNOLOGIES,DS2072A,DS2A1,00.03.05"), 2);
    Check("unknown model falls back to 2", ScpiScope.InferAnalogChannels("ACME,SCOPE,1,1"), 2);

    CheckTrue("garbage preamble rejected", !ScpiScope.TryParsePreamble("not,a,preamble", out _));
    CheckTrue("empty preamble rejected", !ScpiScope.TryParsePreamble("", out _));
}

// ── Fake instrument over a real socket ───────────────────────────────────────
Console.WriteLine();
Console.WriteLine("=== fake instrument over TCP ===");

const string IDN = "RIGOL TECHNOLOGIES,MSO2302A,DS2Axxxxxxxx,00.03.05";
const int POINTS = 1400;

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;

var server = Task.Run(async () =>
{
    using var client = await listener.AcceptTcpClientAsync();
    using var stream = client.GetStream();
    var buf = new byte[4096];
    var pending = new StringBuilder();

    while (true)
    {
        int n = await stream.ReadAsync(buf);
        if (n <= 0) return;
        pending.Append(Encoding.ASCII.GetString(buf, 0, n));

        while (true)
        {
            string all = pending.ToString();
            int nl = all.IndexOf('\n');
            if (nl < 0) break;
            string cmd = all[..nl].Trim();
            pending.Remove(0, nl + 1);

            if (cmd == "*IDN?")
            {
                byte[] r = Encoding.ASCII.GetBytes(IDN + "\n");
                await stream.WriteAsync(r);
            }
            else if (cmd.StartsWith(":CHAN") && cmd.EndsWith(":DISP?"))
            {
                // CH1 and CH2 on, CH3/CH4 off (a 2-channel scope).
                bool on = cmd.Contains("CHAN1") || cmd.Contains("CHAN2");
                await stream.WriteAsync(Encoding.ASCII.GetBytes((on ? "1" : "0") + "\n"));
            }
            else if (cmd == ":WAV:PRE?")
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "0,0,1400,1,5.000000e-07,-3.500000e-04,700,8.000000e-03,-100,127\n"));
            }
            else if (cmd == ":WAV:DATA?")
            {
                // A ramp, so every sample is individually checkable.
                var payload = new byte[POINTS];
                for (int i = 0; i < POINTS; i++) payload[i] = (byte)(i % 256);

                var head = Encoding.ASCII.GetBytes($"#9{POINTS:000000000}");
                await stream.WriteAsync(head);
                await stream.WriteAsync(payload);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\n"));
            }
            // Anything else is a set-command with no reply.
        }
    }
});

try
{
    using var transport = new TcpScpiTransport("127.0.0.1", port, 3000);
    var scope = new ScpiScope(transport);
    scope.Open();

    CheckTrue("*IDN? round-tripped", scope.Identity == IDN);

    uint mask = scope.QueryEnabledChannels();
    Check("enabled channel mask", mask, 0b0011);

    var wf = scope.ReadChannel(1);
    CheckTrue("waveform returned", wf != null);
    if (wf != null)
    {
        Check("sample count", wf.Count, POINTS);
        Check("declared sample rate (Hz)", wf.SampleRateHz, 2_000_000, 1);

        // Same conversion as above, applied through the whole stack.
        Check("sample[0] volts",   wf.Volts[0],   (0   - (-100) - 127) * 0.008, 1e-5);
        Check("sample[127] volts", wf.Volts[127], (127 - (-100) - 127) * 0.008, 1e-5);
        Check("sample[255] volts", wf.Volts[255], (255 - (-100) - 127) * 0.008, 1e-5);
        Check("sample[256] wraps to 0", wf.Volts[256], (0 - (-100) - 127) * 0.008, 1e-5);

        // The block must not leak its header or trailing newline into the data.
        bool ramp = true;
        for (int i = 0; i < POINTS; i++)
        {
            double expect = ((i % 256) - (-100) - 127) * 0.008;
            if (Math.Abs(wf.Volts[i] - expect) > 1e-5) { ramp = false; break; }
        }
        CheckTrue("entire 1400-point block decoded without offset", ramp);
    }

    // A second read on the same link must work (modal state, no re-open).
    var wf2 = scope.ReadChannel(2);
    CheckTrue("second channel read on the same connection", wf2 != null && wf2.Count == POINTS);
}
catch (Exception ex)
{
    failures++;
    Console.WriteLine($"FAIL  transport threw: {ex.GetType().Name}: {ex.Message}");
}
finally
{
    listener.Stop();
}

// ── VISA runtime detection must not throw when absent ────────────────────────
Console.WriteLine();
Console.WriteLine("=== VISA absence is handled ===");
try
{
    bool present = VisaScpiTransport.RuntimeAvailable;
    var resources = VisaScpiTransport.ListResources();
    Console.WriteLine($"PASS  runtime probe returned without throwing (present={present}, " +
                      $"{resources.Length} resource(s))");
}
catch (Exception ex)
{
    failures++;
    Console.WriteLine($"FAIL  VISA probe threw {ex.GetType().Name} — it must degrade quietly");
}

// ── Optional: the same code against a real instrument ────────────────────────
if (args.Length > 0)
{
    Console.WriteLine();
    Console.WriteLine("=== real instrument ===");
    failures += ScpiTests.RealScope.Run(args[0], args.Length > 1 ? int.Parse(args[1]) : 5555);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL SCPI CHECKS PASSED" : $"{failures} SCPI CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
