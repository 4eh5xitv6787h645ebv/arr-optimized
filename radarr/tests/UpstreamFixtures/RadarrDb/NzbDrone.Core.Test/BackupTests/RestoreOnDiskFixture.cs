using System.Data.SQLite;
using System.IO;
using System.Text;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.BackupTests
{
    // Runs an uploaded restore through BackupService.Restore and the startup DatabaseRestorationService.Restore
    // against real files, to show what happens to the live database.
    [TestFixture]
    public class RestoreOnDiskFixture : CoreTest
    {
        private const string LiveDatabase = "SQLite format 3\0live-database";
        private const string LiveWal = "live-wal";
        private const string RestoredDatabase = "SQLite format 3\0restored-database";

        private string _appDataFolder;
        private string _databasePath;
        private string _configPath;

        [SetUp]
        public void Setup()
        {
            _appDataFolder = Path.Combine(TempFolder, "appdata");
            Directory.CreateDirectory(_appDataFolder);

            Mocker.GetMock<IAppFolderInfo>().SetupGet(c => c.TempFolder).Returns(TempFolder);
            Mocker.GetMock<IAppFolderInfo>().SetupGet(c => c.AppDataFolder).Returns(_appDataFolder);

            _databasePath = Mocker.GetMock<IAppFolderInfo>().Object.GetDatabase();
            _configPath = Mocker.GetMock<IAppFolderInfo>().Object.GetConfigPath();

            var disk = Mocker.GetMock<IDiskProvider>();
            disk.Setup(s => s.FileExists(It.IsAny<string>())).Returns<string>(File.Exists);
            disk.Setup(s => s.FolderExists(It.IsAny<string>())).Returns<string>(Directory.Exists);
            disk.Setup(s => s.DeleteFile(It.IsAny<string>())).Callback<string>(File.Delete);
            disk.Setup(s => s.DeleteFolder(It.IsAny<string>(), It.IsAny<bool>())).Callback<string, bool>(Directory.Delete);
            disk.Setup(s => s.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Callback<string, string, bool>(File.Move);
            disk.Setup(s => s.OpenReadStream(It.IsAny<string>())).Returns<string>(p => new FileStream(p, FileMode.Open, FileAccess.Read));

            Write(_databasePath, LiveDatabase);
            Write(_databasePath + "-wal", LiveWal);
            Write(_configPath, "<Config><Port>7878</Port></Config>");
        }

        private static void Write(string path, string contents)
        {
            File.WriteAllText(path, contents, Encoding.ASCII);
        }

        private string GivenUpload(string extension, string contents)
        {
            var path = Path.Combine(TempFolder, "radarr_backup_restore" + extension);
            Write(path, contents);

            return path;
        }

        private void RestoreAndRestart(string upload)
        {
            try
            {
                Mocker.Resolve<BackupService>().Restore(upload);
            }
            catch (RestoreBackupFailedException)
            {
                // Rejected upload: the API returns an error and nothing is staged.
            }

            Mocker.Resolve<DatabaseRestorationService>().Restore();
        }

        [Test]
        public void uploading_config_xml_should_restore_config_and_keep_live_database()
        {
            var upload = GivenUpload(".xml", "<Config><Port>9999</Port></Config>");

            RestoreAndRestart(upload);

            File.ReadAllText(_databasePath).Should().Be(LiveDatabase);
            File.ReadAllText(_configPath).Should().Contain("9999");
        }

        [Test]
        public void uploading_non_sqlite_db_file_should_keep_live_database()
        {
            var upload = GivenUpload(".db", "this is not a database");

            RestoreAndRestart(upload);

            File.ReadAllText(_databasePath).Should().Be(LiveDatabase);
            File.ReadAllText(_databasePath + "-wal").Should().Be(LiveWal);
        }

        [Test]
        public void uploading_logs_db_should_keep_live_database()
        {
            var upload = Path.Combine(TempFolder, "radarr_backup_restore.db");

            using (var connection = new SQLiteConnection($"Data Source={upload};Pooling=False"))
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE \"VersionInfo\" (\"Version\" INTEGER); CREATE TABLE \"Logs\" (\"Id\" INTEGER PRIMARY KEY);";
                    command.ExecuteNonQuery();
                }
            }

            RestoreAndRestart(upload);

            File.ReadAllText(_databasePath).Should().Be(LiveDatabase);
            File.ReadAllText(_databasePath + "-wal").Should().Be(LiveWal);
        }

        [Test]
        public void restoring_valid_database_should_keep_previous_database_until_startup_completes()
        {
            var upload = GivenUpload(".db", RestoredDatabase);

            RestoreAndRestart(upload);

            File.ReadAllText(_databasePath).Should().Be(RestoredDatabase);
            File.Exists(_databasePath + "-wal").Should().BeFalse();
            File.ReadAllText(_databasePath + ".pre-restore").Should().Be(LiveDatabase);
            File.ReadAllText(_databasePath + ".pre-restore-wal").Should().Be(LiveWal);

            var handler = Mocker.Resolve<DatabaseRestorationService>() as IHandleAsync<ApplicationStartedEvent>;
            handler.Should().NotBeNull();
            handler.HandleAsync(new ApplicationStartedEvent());

            File.Exists(_databasePath + ".pre-restore").Should().BeFalse();
            File.Exists(_databasePath + ".pre-restore-wal").Should().BeFalse();
            File.ReadAllText(_databasePath).Should().Be(RestoredDatabase);
        }
    }
}
