using Microsoft.Data.Sqlite;
using VoiceBridge.Core.Importing;

namespace VoiceBridge.Storage;

public sealed class SqliteTakeoutImportStoreFactory : ITakeoutImportStoreFactory
{
    public ITakeoutImportStore Create(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // FileMode.CreateNew is deliberately used so a pre-existing database is never replaced.
        var createdDatabase = false;
        try
        {
            using (new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                createdDatabase = true;
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true,
                Pooling = false
            }.ToString());
            return new SqliteTakeoutImportStore(connection, fullPath);
        }
        catch
        {
            if (createdDatabase && File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            throw;
        }
    }
}
