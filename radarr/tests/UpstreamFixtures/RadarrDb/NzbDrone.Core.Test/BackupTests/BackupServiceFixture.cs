using System.Data.SQLite;
using System.IO;
using System.Text;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.BackupTests
{
    [TestFixture]
    public class BackupServiceFixture : CoreTest<BackupService>
    {
        private string _configPath;
        private string _databaseRestorePath;
        private string _extractPath;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IAppFolderInfo>().SetupGet(c => c.TempFolder).Returns(TempFolder);
            Mocker.GetMock<IAppFolderInfo>().SetupGet(c => c.AppDataFolder).Returns(@"C:\ProgramData\Radarr".AsOsAgnostic());

            _configPath = Mocker.GetMock<IAppFolderInfo>().Object.GetConfigPath();
            _databaseRestorePath = Mocker.GetMock<IAppFolderInfo>().Object.GetDatabaseRestore();
            _extractPath = Path.Combine(TempFolder, "radarr_backup_restore");

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.OpenReadStream(It.IsAny<string>()))
                  .Returns<string>(path => new FileStream(path, FileMode.Open, FileAccess.Read));
        }

        private string GivenFile(string fileName, string contents)
        {
            Directory.CreateDirectory(TempFolder);

            var path = Path.Combine(TempFolder, fileName);
            File.WriteAllText(path, contents, Encoding.ASCII);

            return path;
        }

        private string GivenSqliteDatabase(string fileName, string journalMode = "TRUNCATE", params string[] tables)
        {
            Directory.CreateDirectory(TempFolder);

            var path = Path.Combine(TempFolder, fileName);

            if (tables.Length == 0)
            {
                tables = new[] { "VersionInfo", "Config", "Movies" };
            }

            using (var connection = new SQLiteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"PRAGMA journal_mode={journalMode};";

                    foreach (var table in tables)
                    {
                        command.CommandText += $"CREATE TABLE \"{table}\" (\"Id\" INTEGER PRIMARY KEY);";
                    }

                    command.ExecuteNonQuery();
                }
            }

            return path;
        }

        private string GivenUnreadableSqliteFile(string fileName)
        {
            // Valid header, but SQLite cannot open it (page size 0).
            return GivenFile(fileName, "SQLite format 3\0" + new string('\0', 84));
        }

        private string GivenXmlFile(string fileName)
        {
            return GivenFile(fileName, "<Config><Port>8989</Port></Config>");
        }

        private void GivenExtractFolderExists()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(_extractPath))
                  .Returns(true);
        }

        private void GivenZipContents(params string[] files)
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFiles(_extractPath, false))
                  .Returns(files);
        }

        [Test]
        public void should_restore_uploaded_xml_as_config()
        {
            var path = GivenXmlFile("radarr_backup_restore.xml");

            Subject.Restore(path);

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(path, _configPath, true), Times.Once());
            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), _databaseRestorePath, It.IsAny<bool>()), Times.Never());
        }

        [TestCase("<Config><Port>8989")]
        [TestCase("<Settings><Port>8989</Port></Settings>")]
        [TestCase("SQLite format 3")]
        [TestCase("<Root><Config><Port>8989</Port></Config><Config /></Root>")]
        public void should_not_restore_uploaded_xml_that_is_not_a_config_file(string contents)
        {
            var path = GivenFile("radarr_backup_restore.xml", contents);

            Assert.Throws<RestoreBackupFailedException>(() => Subject.Restore(path));

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_restore_uploaded_sqlite_database()
        {
            var path = GivenSqliteDatabase("radarr_backup_restore.db");

            Subject.Restore(path);

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(path, _databaseRestorePath, true), Times.Once());
        }

        [Test]
        public void should_not_restore_uploaded_database_that_is_not_sqlite()
        {
            var path = GivenXmlFile("radarr_backup_restore.db");

            Assert.Throws<RestoreBackupFailedException>(() => Subject.Restore(path));

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_not_restore_empty_database()
        {
            var path = GivenFile("radarr_backup_restore.db", "");

            Assert.Throws<RestoreBackupFailedException>(() => Subject.Restore(path));

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_restore_config_and_database_from_zip()
        {
            var config = GivenXmlFile("config.xml");
            var database = GivenSqliteDatabase("radarr.db");

            GivenZipContents(config, database);

            Subject.Restore("radarr_backup.zip");

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(config, _configPath, true), Times.Once());
            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(database, _databaseRestorePath, true), Times.Once());
        }

        [Test]
        public void should_not_restore_database_from_zip_that_is_not_sqlite()
        {
            var database = GivenXmlFile("radarr.db");

            GivenZipContents(database);

            Assert.Throws<RestoreBackupFailedException>(() => Subject.Restore("radarr_backup.zip"));

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), _databaseRestorePath, It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_not_restore_config_from_zip_when_database_is_not_sqlite()
        {
            var config = GivenXmlFile("config.xml");
            var database = GivenXmlFile("radarr.db");

            GivenZipContents(config, database);

            Assert.Throws<RestoreBackupFailedException>(() => Subject.Restore("radarr_backup.zip"));

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_not_restore_database_from_zip_when_config_is_invalid()
        {
            var config = GivenFile("config.xml", "not xml");
            var database = GivenSqliteDatabase("radarr.db");

            GivenZipContents(config, database);

            Assert.Throws<RestoreBackupFailedException>(() => Subject.Restore("radarr_backup.zip"));

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_delete_previously_extracted_files_before_extracting()
        {
            GivenExtractFolderExists();
            GivenZipContents(GivenSqliteDatabase("radarr.db"));

            Mocker.GetMock<IArchiveService>()
                  .Setup(s => s.Extract("radarr_backup.zip", _extractPath))
                  .Callback(() => Mocker.GetMock<IDiskProvider>().Verify(v => v.DeleteFolder(_extractPath, true), Times.Once()));

            Subject.Restore("radarr_backup.zip");

            Mocker.GetMock<IArchiveService>().Verify(v => v.Extract("radarr_backup.zip", _extractPath), Times.Once());
        }

        [Test]
        public void should_delete_extracted_files_when_restore_fails()
        {
            GivenZipContents(GivenXmlFile("radarr.db"));

            Mocker.GetMock<IArchiveService>()
                  .Setup(s => s.Extract("radarr_backup.zip", _extractPath))
                  .Callback(GivenExtractFolderExists);

            Assert.Throws<RestoreBackupFailedException>(() => Subject.Restore("radarr_backup.zip"));

            Mocker.GetMock<IDiskProvider>().Verify(v => v.DeleteFolder(_extractPath, true), Times.Once());
        }

        [TestCase("SQLite format 3X")]
        [TestCase("SQLite format 2\0")]
        [TestCase("SQLite junk")]
        public void should_not_restore_uploaded_database_with_invalid_sqlite_header(string header)
        {
            var path = GivenFile("radarr_backup_restore.db", header + new string('\0', 84));

            Assert.Throws<RestoreBackupFailedException>(() => Subject.Restore(path));

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_not_restore_uploaded_sqlite_database_that_is_not_a_radarr_database()
        {
            // e.g. logs.db uploaded by mistake: valid SQLite, but no Movies/Config tables
            var path = GivenSqliteDatabase("radarr_backup_restore.db", "TRUNCATE", "Logs", "UpdateHistory", "VersionInfo");

            Assert.Throws<RestoreBackupFailedException>(() => Subject.Restore(path));

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [TestCase("WAL", "radarr_backup_restore.db")]
        [TestCase("TRUNCATE", "folder with space #1/radarr_backup_restore.db")]
        public void should_read_tables_of_uploaded_database(string journalMode, string fileName)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(TempFolder, fileName)));

            var path = GivenSqliteDatabase(fileName, journalMode, "Logs", "VersionInfo");

            Assert.Throws<RestoreBackupFailedException>(() => Subject.Restore(path));

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_restore_uploaded_wal_mode_database_without_leaving_files_behind()
        {
            var path = GivenSqliteDatabase("radarr_backup_restore.db", "WAL");

            Subject.Restore(path);

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(path, _databaseRestorePath, true), Times.Once());
            File.Exists(path + "-wal").Should().BeFalse();
            File.Exists(path + "-shm").Should().BeFalse();
        }

        [Test]
        public void should_restore_uploaded_database_with_valid_header_that_cannot_be_inspected()
        {
            var path = GivenUnreadableSqliteFile("radarr_backup_restore.db");

            Subject.Restore(path);

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(path, _databaseRestorePath, true), Times.Once());
        }

        [Test]
        public void should_throw_if_zip_contains_neither_config_nor_database()
        {
            GivenZipContents(GivenFile("INFO", "v7.0.3.10918"));

            var exception = Assert.Throws<RestoreBackupFailedException>(() => Subject.Restore("radarr_backup.zip"));

            exception.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_restore_legacy_nzbdrone_database_from_zip()
        {
            var database = GivenSqliteDatabase("nzbdrone.db");

            GivenZipContents(database);

            Subject.Restore("radarr_backup.zip");

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(database, _databaseRestorePath, true), Times.Once());
        }

        [Test]
        public void should_delete_extracted_files_after_successful_restore()
        {
            GivenZipContents(GivenXmlFile("config.xml"), GivenSqliteDatabase("radarr.db"));

            Mocker.GetMock<IArchiveService>()
                  .Setup(s => s.Extract("radarr_backup.zip", _extractPath))
                  .Callback(GivenExtractFolderExists);

            Subject.Restore("radarr_backup.zip");

            Mocker.GetMock<IDiskProvider>().Verify(v => v.DeleteFolder(_extractPath, true), Times.Once());
        }
    }
}
