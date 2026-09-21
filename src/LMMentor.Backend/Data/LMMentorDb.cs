using LiteDB;

namespace LMMentor.Backend.Data;

/// <summary>
/// Wrapper do banco LiteDB (arquivo único). O caminho é configurável via LMMENTOR_DB_PATH,
/// com padrão em lmmentor.db ao lado do executável.
/// </summary>
public sealed class LMMentorDb : IDisposable
{
    public LiteDatabase Db { get; }

    public LMMentorDb(string dbPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Db = new LiteDatabase(dbPath);
    }

    public void Dispose() => Db.Dispose();
}
