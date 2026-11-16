using Microsoft.Data.Sqlite;

namespace ScreenAssistant.Server;

internal static class LicenseDatabaseBackup
{
    internal static void Create(string sourcePath, string destinationPath)
    {
        var destination = Path.GetFullPath(destinationPath);
        if (File.Exists(destination))
            throw new IOException("The backup destination already exists.");

        var directory = Path.GetDirectoryName(destination)!;
        var staging = Path.Combine(directory, $".screenassistant-backup-{Guid.NewGuid():N}.db");

        using var source = Open(Path.GetFullPath(sourcePath), SqliteOpenMode.ReadOnly);
        using (File.Open(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try
        {
            using (var copy = Open(staging, SqliteOpenMode.ReadWrite))
            {
                source.BackupDatabase(copy);
                using var check = copy.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                if (check.ExecuteScalar() is not "ok")
                    throw new InvalidDataException("The SQLite backup failed its integrity check.");

                check.CommandText = "PRAGMA foreign_key_check";
                using var violations = check.ExecuteReader();
                if (violations.Read())
                    throw new InvalidDataException("The SQLite backup contains a foreign key violation.");
            }

            File.Move(staging, destination);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
