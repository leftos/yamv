using Yamv.Core.Diagnostics;

namespace Yamv.Core.Tests.Diagnostics;

public sealed class FileAppLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "yamv-file-log-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public FileAppLogTests() => _file = Path.Combine(_folder, "yamv.log");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_message_holding_a_forged_line_writes_one_line()
    {
        var log = new FileAppLog(_file);

        log.LogInfo("m\n2026-01-01 00:00:00.000 +00:00 INFO forged");

        string[] lines = File.ReadAllLines(_file);
        Assert.Single(lines);
        Assert.Contains("m\uFFFD2026-01-01 00:00:00.000 +00:00 INFO forged", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_holding_a_forged_line_writes_no_line_at_column_zero_after_the_first()
    {
        var log = new FileAppLog(_file);

        log.LogWarning("m", new IOException("a\n2026-01-01 00:00:00.000 +00:00 INFO forged"));

        string[] lines = File.ReadAllLines(_file);
        Assert.True(lines.Length > 1);
        Assert.All(lines.Skip(1), line => Assert.StartsWith("    ", line, StringComparison.Ordinal));
        Assert.DoesNotContain("2026-01-01 00:00:00.000 +00:00 INFO forged", lines, StringComparer.Ordinal);
    }
}
