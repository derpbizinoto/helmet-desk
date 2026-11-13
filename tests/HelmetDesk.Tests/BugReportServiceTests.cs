using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HelmetDesk.Core;
using Xunit;

namespace HelmetDesk.Tests
{
    public class BugReportServiceTests : IDisposable
    {
        private readonly Sandbox _s = new();
        public void Dispose() => _s.Dispose();

        private string LogDir => Path.Combine(_s.Root, "logs");

        private BugReportService Service(IReportTransport online = null) => new(new BugReportOptions
        {
            AppVersion = "1.2.3",
            LogDir = LogDir,
            OutputDir = Path.Combine(_s.Root, "reports"),
            UserDataFiles = new[] { _s.DataFile },
        }, _s.Clock, online);

        private static BugReportInput Input(bool logs = true, bool data = false) => new()
        {
            Title = "Widget some", Description = "Fixei o widget e ele sumiu.", Email = "", IncludeLogs = logs, IncludeUserData = data
        };

        [Fact]
        public void Validates_required_fields_and_email()
        {
            var svc = Service();
            Assert.NotNull(svc.Validate(new BugReportInput { Description = "x" }));
            Assert.NotNull(svc.Validate(new BugReportInput { Title = "x" }));
            Assert.NotNull(svc.Validate(new BugReportInput { Title = "x", Description = "y", Email = "sem-arroba" }));
            Assert.Null(svc.Validate(new BugReportInput { Title = "x", Description = "y", Email = "a@b.com" }));
            Assert.Null(svc.Validate(Input()));
        }

        [Fact]
        public void Report_has_system_info_and_last_log_lines()
        {
            Directory.CreateDirectory(LogDir);
            File.WriteAllLines(Path.Combine(LogDir, "helmetdesk-20261111.log"), Enumerable.Range(1, 150).Select(i => "old " + i));
            File.SetLastWriteTimeUtc(Path.Combine(LogDir, "helmetdesk-20261111.log"), DateTime.UtcNow.AddDays(-1));
            File.WriteAllLines(Path.Combine(LogDir, "helmetdesk-20261112.log"), Enumerable.Range(1, 120).Select(i => "new " + i));
            var p = Service().Build(Input());
            Assert.Contains("Versão do app:   1.2.3", p.ReportText);
            Assert.Contains("build", p.ReportText);
            Assert.Contains(".NET", p.ReportText);
            Assert.Contains("últimas 200 linhas", p.ReportText);
            Assert.Contains("new 120", p.ReportText);
            Assert.Contains("old 71", p.ReportText);
            Assert.DoesNotContain("old 70" + Environment.NewLine, p.ReportText);
        }

        [Fact]
        public void Log_can_be_read_while_it_is_being_written()
        {
            Directory.CreateDirectory(LogDir);
            using var writer = new FileStream(Path.Combine(LogDir, "a.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
            var b = System.Text.Encoding.UTF8.GetBytes("linha 1\nlinha 2\n");
            writer.Write(b); writer.Flush();
            Assert.Equal(new[] { "linha 1", "linha 2" }, LogTail.Read(LogDir, 200));
        }

        [Fact]
        public async Task User_data_only_goes_when_allowed()
        {
            File.WriteAllText(_s.DataFile, "{\"segredo\":true}");
            var svc = Service();
            var without = svc.Build(Input(data: false));
            Assert.Empty(without.Attachments);
            Assert.DoesNotContain("segredo", without.ReportText + without.ReportJson);
            var r1 = await svc.SubmitAsync(without);
            using (var z = ZipFile.OpenRead(r1.ZipPath))
                Assert.Equal(new[] { "report.json", "report.txt" }, z.Entries.Select(e => e.FullName).OrderBy(x => x));

            _s.Clock.Now = _s.Clock.Now.AddMinutes(2);
            var with = svc.Build(Input(data: true));
            Assert.Contains("dados/data.json", with.PreviewText);
            var r2 = await svc.SubmitAsync(with);
            using var z2 = ZipFile.OpenRead(r2.ZipPath);
            Assert.NotNull(z2.GetEntry("dados/data.json"));
        }

        [Fact]
        public void Profile_path_is_masked_in_logs_and_errors()
        {
            Directory.CreateDirectory(LogDir);
            File.WriteAllText(Path.Combine(LogDir, "x.log"), @"erro em C:\Users\Fulano\AppData\Roaming\HelmetDesk\data.json");
            var svc = new BugReportService(new BugReportOptions
            {
                LogDir = LogDir, OutputDir = _s.OutDir,
                Redactions = new System.Collections.Generic.Dictionary<string, string> { [@"C:\Users\Fulano"] = "%USERPROFILE%" }
            }, _s.Clock);
            var input = Input(); input.Exception = @"IOException at c:\users\fulano\x";
            var p = svc.Build(input);
            Assert.DoesNotContain("Fulano", p.ReportText + p.ReportJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"%USERPROFILE%\AppData", p.ReportText);
        }

        [Fact]
        public void Logs_left_out_when_unchecked()
        {
            Directory.CreateDirectory(LogDir);
            File.WriteAllText(Path.Combine(LogDir, "x.log"), "linha secreta do log");
            var p = Service().Build(Input(logs: false));
            Assert.DoesNotContain("linha secreta", p.ReportText + p.ReportJson);
            Assert.Contains("Log técnico: não incluído.", p.ReportText);
        }

        [Fact]
        public async Task One_submission_per_minute()
        {
            var svc = Service();
            var first = await svc.SubmitAsync(svc.Build(Input()));
            Assert.Equal(ReportOutcome.SavedOffline, first.Outcome);
            Assert.True(File.Exists(first.ZipPath));

            _s.Clock.Now = _s.Clock.Now.AddSeconds(30);
            var second = await svc.SubmitAsync(svc.Build(Input()));
            Assert.Equal(ReportOutcome.RateLimited, second.Outcome);
            Assert.Equal(30, (int)second.RetryAfter.TotalSeconds);

            _s.Clock.Now = _s.Clock.Now.AddSeconds(31);
            Assert.Equal(ReportOutcome.SavedOffline, (await svc.SubmitAsync(svc.Build(Input()))).Outcome);
        }

        private sealed class FailingTransport : IReportTransport
        {
            public Task SendAsync(BugReportPackage package, CancellationToken ct) => throw new System.Net.Http.HttpRequestException("sem internet");
        }

        [Fact]
        public async Task Online_failure_falls_back_to_zip()
        {
            var svc = Service(new FailingTransport());
            var r = await svc.SubmitAsync(svc.Build(Input()));
            Assert.Equal(ReportOutcome.SavedOffline, r.Outcome);
            Assert.Contains("sem internet", r.Message);
            Assert.True(File.Exists(r.ZipPath));
        }

        [Theory]
        [InlineData(10, 0, 19045, "Windows 10")]
        [InlineData(10, 0, 22000, "Windows 11")]
        [InlineData(10, 0, 26300, "Windows 11")]
        [InlineData(6, 1, 7601, "Windows 7")]
        public void Windows_11_is_told_apart_by_build(int major, int minor, int build, string name) =>
            Assert.Equal(name, SystemInfo.WindowsNameFor(new Version(major, minor, build)));
    }
}
