namespace ScreenAssistant.Server;

internal sealed partial class LicenseStore
{
    internal IReadOnlyList<OperatorAuditEntry> ReadOperatorAudit(long after)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, action, license_id, reference_id, credits, occurred_at
            FROM operator_audit WHERE id > $after
            ORDER BY id LIMIT 100
            """;
        cmd.Parameters.AddWithValue("$after", after);
        using var rows = cmd.ExecuteReader();
        var entries = new List<OperatorAuditEntry>();
        while (rows.Read())
        {
            entries.Add(new(
                rows.GetInt64(0),
                rows.GetString(1),
                Guid.ParseExact(rows.GetString(2), "N"),
                rows.IsDBNull(3) ? null : Guid.ParseExact(rows.GetString(3), "N"),
                rows.IsDBNull(4) ? null : rows.GetInt64(4),
                DateTimeOffset.FromUnixTimeSeconds(rows.GetInt64(5))));
        }
        return entries;
    }
}

internal sealed record OperatorAuditEntry(long Id, string Action, Guid LicenseId, Guid? ReferenceId,
    long? Credits, DateTimeOffset OccurredAt);
