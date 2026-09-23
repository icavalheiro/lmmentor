using LMMentor.Backend.Data;

namespace LMMentor.Backend.UnitTests.TestSupport;

/// <summary>Banco LiteDB em arquivo temporário, descartado ao final do teste.</summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _path;

    public TempDatabase()
    {
        _path = Path.Combine(Path.GetTempPath(), $"lmmentor-tests-{Guid.NewGuid():N}.db");
        Db = new LMMentorDb(_path);
    }

    public LMMentorDb Db { get; }

    public void Dispose()
    {
        Db.Dispose();

        foreach (var file in new[] { _path, _path + "-log" })
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Arquivo ainda travado pelo SO: o diretório temporário é limpo depois.
        }
    }
}
