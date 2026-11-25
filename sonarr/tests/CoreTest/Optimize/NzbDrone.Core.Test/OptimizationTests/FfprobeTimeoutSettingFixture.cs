using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimization;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;
using NzbDrone.Test.Common.Categories;

namespace NzbDrone.Core.Test.OptimizationTests
{
    // The "ffprobe timeout" value with the real bundled ffprobe: 0 runs the stock calls without a timeout and gives
    // the same media info, any other value stops a hung ffprobe and counts it on the status page.
    [TestFixture]
    [DiskAccessTest]
    [NonParallelizable]
    [Platform(Exclude = "Win")]
    public class FfprobeTimeoutSettingFixture : CoreTest<VideoFileInfoReader>
    {
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
            OptimizeSettings.Current = null;

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(It.IsAny<string>()))
                  .Returns<string>(p => new FileInfo(p).Exists ? new FileInfo(p).Length : 1000);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileGetLastWrite(It.IsAny<string>()))
                  .Returns(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        }

        [TearDown]
        public void TearDown()
        {
            OptimizeSettings.Current = null;
            MediaInfoProbeCache.ApplyAll(OptimizeSettings.Current);

            if (_fifo != null)
            {
                KillFfprobeProcessesFor(_fifo);
            }
        }

        private static void Apply(Dictionary<string, object> values)
        {
            var settings = OptimizeSettings.Defaults.With(values);
            OptimizeSettings.Current = settings;
            MediaInfoProbeCache.ApplyAll(settings);
        }

        private static string SamplePath(string name)
        {
            return Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", name);
        }

        private static MediaInfoModel Probe(VideoFileInfoReader reader, string path)
        {
            var probe = Task.Run(() => reader.GetMediaInfo(path));

            // A probe of a sample takes well under a second; 60 s only guards against a hang.
            probe.Wait(TimeSpan.FromSeconds(60)).Should().BeTrue("probing a sample file finishes");

            return probe.Result;
        }

        // Every probe with the timeout off (0) must give the same media info as with a timeout: the stock synchronous
        // FFProbe.Analyse / GetFrames calls, not a cancellation token that is already cancelled.
        [TestCase("H264_sample.mp4")]
        [TestCase("ffprobe_cache_noaudio.mkv")]
        [TestCase("ffprobe_cache_hevc_pq.mp4")]
        public void zero_timeout_should_probe_with_the_stock_calls_and_give_the_same_media_info(string sample)
        {
            var path = SamplePath(sample);
            var timeouts = Interlocked.Read(ref VideoFileInfoReader.Timeouts);

            // Cache off, so every read really runs ffprobe.
            Apply(new Dictionary<string, object> { { "FfprobeCacheCapacity", 0 } });
            var withTimeout = Probe(Subject, path);

            Apply(new Dictionary<string, object> { { "FfprobeCacheCapacity", 0 }, { "FfprobeTimeoutMinutes", 0 } });
            VideoFileInfoReader.ProbeTimeout.Should().Be(TimeSpan.Zero);
            var withoutTimeout = Probe(Subject, path);

            withTimeout.Should().NotBeNull();
            withoutTimeout.Should().NotBeNull("0 means no timeout, not an immediate one");
            withoutTimeout.Should().BeEquivalentTo(withTimeout);
            Interlocked.Read(ref VideoFileInfoReader.Timeouts).Should().Be(timeouts);
            ExceptionVerification.ExpectedErrors(0);
        }

        [Test]
        public void zero_timeout_with_the_cache_on_should_probe_like_stock()
        {
            Apply(new Dictionary<string, object> { { "FfprobeTimeoutMinutes", 0 } });

            Probe(Subject, SamplePath("H264_sample.mp4")).VideoFormat.Should().Be("h264");
            ExceptionVerification.ExpectedErrors(0);
        }

        // A named pipe without a writer makes ffprobe block in open() forever, like a probe of a stalled mount.
        [Test]
        public void hung_ffprobe_should_be_stopped_after_the_saved_timeout_and_counted()
        {
            var folder = GetTempFilePath();
            Directory.CreateDirectory(folder);
            _fifo = Path.Combine(folder, "stalled.mkv");

            using (var mkfifo = Process.Start("mkfifo", new[] { _fifo }))
            {
                mkfifo.WaitForExit();
                mkfifo.ExitCode.Should().Be(0);
            }

            var service = new OptimizeSettingsService(new Mock<IConfigService>().Object, TestLogger);
            var timeouts = Interlocked.Read(ref VideoFileInfoReader.Timeouts);
            var statusBefore = service.GetStatus().Counters.Single(c => c.Name == "ffprobeTimeouts").Value;

            // The page stores whole minutes; a 3 s timeout keeps the test short.
            VideoFileInfoReader.ProbeTimeout = TimeSpan.FromSeconds(3);

            var watch = Stopwatch.StartNew();
            var probe = Task.Run(() => Subject.GetMediaInfo(_fifo));
            var finished = probe.Wait(TimeSpan.FromSeconds(30));
            watch.Stop();

            try
            {
                finished.Should().BeTrue("the hung ffprobe is stopped by the timeout");
                probe.Result.Should().BeNull();
                watch.Elapsed.Should().BeGreaterOrEqualTo(TimeSpan.FromSeconds(2.9));

                Interlocked.Read(ref VideoFileInfoReader.Timeouts).Should().Be(timeouts + 1);
                service.GetStatus().Counters.Single(c => c.Name == "ffprobeTimeouts").Value.Should().Be(statusBefore + 1);
                ExceptionVerification.ExpectedErrors(1);
            }
            finally
            {
                KillFfprobeProcessesFor(_fifo);

                if (!finished)
                {
                    probe.Wait(TimeSpan.FromSeconds(30));
                }
            }
        }

        private static void KillFfprobeProcessesFor(string path)
        {
            if (!Directory.Exists("/proc"))
            {
                return;
            }

            foreach (var directory in Directory.GetDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out var pid))
                {
                    continue;
                }

                try
                {
                    var commandLine = File.ReadAllText(Path.Combine(directory, "cmdline")).Replace('\0', ' ');

                    if (commandLine.Contains("ffprobe") && commandLine.Contains(path))
                    {
                        using var process = Process.GetProcessById(pid);
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception)
                {
                    // Already gone
                }
            }
        }
    }
}
