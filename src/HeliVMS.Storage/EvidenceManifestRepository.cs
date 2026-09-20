using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>證據 manifest 記錄（M53，§14.7 #5）。</summary>
public sealed record EvidenceManifestRecord(
    int Id,
    string DirectoryPath,
    string ManifestJson,
    string Status,
    string CreatedAt,
    string? LastVerifiedAt);

/// <summary>證據 manifest 存取（M53，§14.7 #5）。</summary>
public sealed class EvidenceManifestRepository
{
    private const string Columns =
        "id, directory_path, manifest_json, status, created_at, last_verified_at";

    private readonly SqliteStore _store;

    public EvidenceManifestRepository(SqliteStore store)
    {
        _store = store;
    }

    public int Upsert(string directoryPath, string manifestJson, string status, DateTime? utc = null)
    {
        var now = SqliteStore.Iso(utc ?? DateTime.UtcNow);

        return _store.Query(
            """
            INSERT INTO evidence_manifests (directory_path, manifest_json, status, created_at, last_verified_at)
            VALUES ($d, $m, $s, $t, NULL)
            ON CONFLICT(directory_path) DO UPDATE SET manifest_json = $m, status = $s, last_verified_at = $t;
            SELECT id FROM evidence_manifests WHERE directory_path = $d;
            """,
            static r =>
            {
                r.Read();
                return (int)r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$d", directoryPath);
                cmd.Parameters.AddWithValue("$m", manifestJson);
                cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$t", now);
            });
    }

    public EvidenceManifestRecord? GetByDirectory(string directoryPath)
        => _store.Query(
            $"SELECT {Columns} FROM evidence_manifests WHERE directory_path = $d;",
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$d", directoryPath));

    public IReadOnlyList<EvidenceManifestRecord> List()
        => _store.Query(
            $"SELECT {Columns} FROM evidence_manifests ORDER BY id;",
            static r =>
            {
                var list = new List<EvidenceManifestRecord>();
                while (r.Read())
                {
                    list.Add(Read(r));
                }

                return list;
            });

    public void SetVerified(int id, string status, DateTime? utc = null)
        => _store.Execute(
            """
            UPDATE evidence_manifests
            SET status = $s, last_verified_at = $t
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(utc ?? DateTime.UtcNow));
                cmd.Parameters.AddWithValue("$id", id);
            });

    public void Delete(int id)
        => _store.Execute(
            "DELETE FROM evidence_manifests WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));

    private static EvidenceManifestRecord Read(SqliteDataReader r)
        => new(
            r.GetInt32(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5));
}