// Where the optional real-hardware / real-board test fixtures live.
//
// These used to be absolute paths hardcoded into the checks, which pinned the suite
// to one machine AND published a username, a folder layout and a project name into
// a public repository. Both problems have the same fix: name the location outside
// the source.
//
// Resolution order:
//   1. the EDES_TEST_BOARD environment variable
//   2. tests/local-testdata.txt (gitignored) — first non-comment line
//   3. nothing, and the checks that need it SKIP rather than fail
//
// Skipping rather than failing is deliberate: a missing local fixture is not a
// defect in the code under test, and a suite that goes red on a colleague's machine
// for that reason stops being trusted.

namespace PcbParserTests;

public static class TestData
{
    private const string EnvVar = "EDES_TEST_BOARD";
    private const string LocalFile = "local-testdata.txt";

    private static string? _cached;
    private static bool _resolved;

    /// <summary>Folder holding a real fabrication output set, or null if not configured.</summary>
    public static string? BoardFolder
    {
        get
        {
            if (_resolved) return _cached;
            _resolved = true;
            _cached = Resolve();
            return _cached;
        }
    }

    /// <summary>The first STEP file under BoardFolder, or null. Searched rather than
    /// hardcoded, so the fixture's internal layout is not baked in either.</summary>
    public static string? BoardStepFile
    {
        get
        {
            string? root = BoardFolder;
            if (root == null) return null;
            try
            {
                foreach (string pattern in new[] { "*.step", "*.stp" })
                {
                    var hits = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
                    if (hits.Length > 0)
                    {
                        Array.Sort(hits, StringComparer.OrdinalIgnoreCase);
                        return hits[0];
                    }
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>Message to print when a fixture-dependent check is skipped, naming how to
    /// enable it — a bare "SKIP" leaves the reader with nothing to act on.</summary>
    public static string SkipReason =>
        $"set {EnvVar}, or put the folder path in tests/{LocalFile}, to run this";

    private static string? Resolve()
    {
        string? env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env.Trim('"', ' ')))
            return env.Trim('"', ' ');

        // Walk up looking for tests/local-testdata.txt, so the suite works whether it is
        // run from the repo root or from the test project directory.
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "tests", LocalFile);
                if (!File.Exists(candidate))
                    candidate = Path.Combine(dir.FullName, LocalFile);
                if (!File.Exists(candidate)) continue;

                foreach (string raw in File.ReadAllLines(candidate))
                {
                    string line = raw.Trim().Trim('"');
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    if (Directory.Exists(line)) return line;
                }
            }
        }
        catch { }

        return null;
    }
}
