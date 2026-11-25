using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FFMpegCore;
using FluentAssertions;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;
using NzbDrone.Test.Common.Categories;

namespace NzbDrone.Core.Test.MediaFiles.MediaInfo
{
    // A named pipe without a writer makes ffprobe block in open() forever, like a probe of a stalled mount.
    // Stock waits for ffprobe without a limit, so GetMediaInfo never returns; with the probe timeout ffprobe is
    // killed and the probe fails like any other unreadable file. The timeout is shortened through the internal
    // VideoFileInfoReader.ProbeTimeout, resolved by name so this fixture also compiles against stock.
    [TestFixture]
    [DiskAccessTest]
    [NonParallelizable]
    [Platform(Exclude = "Win")]
    public class VideoFileInfoReaderTimeoutFixture : CoreTest<VideoFileInfoReader>
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        private PropertyInfo _timeoutProperty;
        private object _originalTimeout;
        private string _fifo;

        // NzbDroneLogger's static constructor replaces the NLog configuration. When this fixture is the first to run in a
        // process (a targeted run), that happened after LoggingTest had added the ExceptionVerification target for the
        // first test, so the errors that test checks were not captured. Initialise it before any SetUp runs.
        [OneTimeSetUp]
        public void InitializeLoggerBeforeFirstTest()
        {
            NzbDroneLogger.GetLogger(GetType());
        }

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(It.IsAny<string>()))
                  .Returns(1000);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileGetLastWrite(It.IsAny<string>()))
                  .Returns(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            _timeoutProperty = typeof(VideoFileInfoReader).GetProperty("ProbeTimeout", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            if (_timeoutProperty != null)
            {
                _originalTimeout = _timeoutProperty.GetValue(null);
                _timeoutProperty.SetValue(null, TestTimeout);
            }

            var folder = GetTempFilePath();
            Directory.CreateDirectory(folder);
            _fifo = Path.Combine(folder, "stalled.mkv");

            using var mkfifo = Process.Start("mkfifo", new[] { _fifo });
            mkfifo.WaitForExit();
            mkfifo.ExitCode.Should().Be(0);
        }

        [TearDown]
        public void TearDown()
        {
            if (_timeoutProperty != null)
            {
                _timeoutProperty.SetValue(null, _originalTimeout);
            }

            // VideoFileInfoReader points FFMpegCore at the bundled ffprobe; undo the stub of the tests below
            GlobalFFOptions.Configure(options => options.BinaryFolder = AppDomain.CurrentDomain.BaseDirectory);
            KillFfprobeProcessesFor(_fifo);
        }

        // The ffprobe timeout is a setting on the Optimize page now; its default rose from 5 to 15 minutes.
        [Test]
        public void default_probe_timeout_should_be_fifteen_minutes()
        {
            _timeoutProperty.Should().NotBeNull("the probe timeout exists");
            _originalTimeout.Should().Be(TimeSpan.FromMinutes(15));
        }

        [Test]
        public void hung_ffprobe_should_be_stopped_and_treated_as_a_failed_probe()
        {
            ProbeShouldBeStopped(_fifo);
        }

        // The stub ffprobe below runs the bundled one, except for the calls given: those either hang on the FIFO
        // (an ffprobe blocked in open(), like on a stalled mount) or are delayed by a sleep first.
        private void UseFfprobeStub(string hangWhen = null, string delayWhen = null, double delaySeconds = 0)
        {
            // Create the reader first: its constructor points FFMpegCore at the bundled ffprobe
            Subject.Should().NotBeNull();

            var real = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffprobe");
            var folder = Path.Combine(GetTempFilePath(), "stub");
            Directory.CreateDirectory(folder);
            var stub = Path.Combine(folder, "ffprobe");

            var script = "#!/bin/sh\n" +
                         $"REAL='{real}'\n" +
                         "case \" $* \" in\n" +
                         (hangWhen == null ? string.Empty : $"  *\"{hangWhen}\"*) exec \"$REAL\" -loglevel error '{_fifo}' ;;\n") +
                         (delayWhen == null ? string.Empty : $"  *\"{delayWhen}\"*) sleep {delaySeconds} </dev/null >/dev/null 2>&1 ;;\n") +
                         "esac\n" +
                         "exec \"$REAL\" \"$@\"\n";

            File.WriteAllText(stub, script);
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            GlobalFFOptions.Configure(options => options.BinaryFolder = folder);
        }

        private static string SamplePath(string name)
        {
            return Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", name);
        }

        private void ProbeShouldBeStopped(string path)
        {
            var watch = Stopwatch.StartNew();
            var probe = Task.Run(() => Subject.GetMediaInfo(path));
            var finished = probe.Wait(Patience);
            watch.Stop();

            try
            {
                finished.Should().BeTrue("a hung ffprobe must not block GetMediaInfo for longer than the probe timeout");
                probe.Result.Should().BeNull();
                watch.Elapsed.Should().BeGreaterOrEqualTo(TestTimeout - TimeSpan.FromMilliseconds(100));
                FfprobeProcessesFor(_fifo).Should().BeEmpty("the hung ffprobe is killed");
                LoggedErrors().Should().ContainSingle().Which.Should().Contain("did not finish within");

                ExceptionVerification.ExpectedErrors(1);
            }
            finally
            {
                // A blocked ffprobe that is still there (no timeout, or the kill failed) is killed here, so it does not
                // outlive the test run and the probe can end
                KillFfprobeProcessesFor(_fifo);

                if (!finished)
                {
                    probe.Wait(Patience);
                }

                ExceptionVerification.IgnoreErrors();
            }
        }

        // No audio stream: the 150 MB second pass runs
        [Test]
        public void hung_second_ffprobe_pass_should_be_stopped()
        {
            UseFfprobeStub(hangWhen: "-probesize 150000000");

            ProbeShouldBeStopped(SamplePath("ffprobe_cache_noaudio.mkv"));
        }

        // PQ transfer: the -show_frames pass runs
        [Test]
        public void hung_frames_pass_should_be_stopped()
        {
            UseFfprobeStub(hangWhen: "-show_frames");

            ProbeShouldBeStopped(SamplePath("ffprobe_cache_hevc_pq.mp4"));
        }

        // One budget for all ffprobe runs of a probe: two passes that each fit the timeout, but not together.
        [Test]
        public void all_ffprobe_passes_should_share_one_timeout()
        {
            var delay = TestTimeout.TotalSeconds * 2 / 3;
            UseFfprobeStub(delayWhen: "-probesize ", delaySeconds: delay);

            var watch = Stopwatch.StartNew();
            var result = Subject.GetMediaInfo(SamplePath("ffprobe_cache_noaudio.mkv"));
            watch.Stop();

            result.Should().BeNull("the second pass would end after the shared budget");
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(delay * 2) - TimeSpan.FromMilliseconds(200));
            LoggedErrors().Should().ContainSingle().Which.Should().Contain("did not finish within");
            ExceptionVerification.ExpectedErrors(1);
        }

        // Error messages captured by the test log target so far
        private static string[] LoggedErrors()
        {
            var logs = (List<LogEventInfo>)typeof(ExceptionVerification)
                .GetField("_logs", BindingFlags.Static | BindingFlags.NonPublic)
                .GetValue(null);

            lock (logs)
            {
                return logs.Where(l => l.Level == LogLevel.Error).Select(l => l.FormattedMessage).ToArray();
            }
        }

        [Test]
        public void stubbed_ffprobe_without_hang_should_still_give_media_info()
        {
            UseFfprobeStub(hangWhen: "-never-used-argument");

            Subject.GetMediaInfo(SamplePath("ffprobe_cache_noaudio.mkv")).VideoFormat.Should().Be("h264");
            Subject.GetMediaInfo(SamplePath("ffprobe_cache_hevc_pq.mp4")).VideoHdrFormat.Should().Be(HdrFormat.Hdr10);
        }

        [Test]
        public void ffprobe_that_finishes_should_not_be_affected_by_the_timeout()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", "H264_sample.mp4");

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("h264");
        }

        private static string[] FfprobeProcessesFor(string path)
        {
            return FfprobePidsFor(path).Select(p => p.CommandLine).ToArray();
        }

        private static void KillFfprobeProcessesFor(string path)
        {
            foreach (var (pid, _) in FfprobePidsFor(path))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception)
                {
                    // Already gone
                }
            }
        }

        private static (int Pid, string CommandLine)[] FfprobePidsFor(string path)
        {
            if (path == null || !Directory.Exists("/proc"))
            {
                return Array.Empty<(int, string)>();
            }

            return Directory.GetDirectories("/proc")
                .Select(d => (Pid: int.TryParse(Path.GetFileName(d), out var pid) ? pid : -1, File: Path.Combine(d, "cmdline")))
                .Where(d => d.Pid > 0)
                .Select(d =>
                {
                    try
                    {
                        return (d.Pid, CommandLine: File.ReadAllText(d.File).Replace('\0', ' '));
                    }
                    catch (Exception)
                    {
                        return (d.Pid, CommandLine: string.Empty);
                    }
                })
                .Where(c => c.CommandLine.Contains("ffprobe") && c.CommandLine.Contains(path))
                .ToArray();
        }
    }
}
