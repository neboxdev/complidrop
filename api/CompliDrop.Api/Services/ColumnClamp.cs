namespace CompliDrop.Api.Services;

/// <summary>
/// The ONE surrogate-safe truncation to a bounded <c>varchar(n)</c> column (#372).
/// <para/>
/// Npgsql does not truncate: writing an over-length string to a bounded column fails the whole
/// <c>SaveChanges</c> with Postgres <c>22001</c> (value too long) — and because the audit row is
/// added to the SAME unit of work as the business mutation, that takes the mutation down with it.
/// Any value that reaches a bounded column WITHOUT a length the writer controls must pass through
/// here first.
/// </summary>
public static class ColumnClamp
{
    /// <summary>
    /// Returns <paramref name="value"/> unchanged when it fits <paramref name="maxLength"/>,
    /// otherwise its first <paramref name="maxLength"/> characters.
    /// </summary>
    public static string? To(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength) return value;
        // Back off one code unit when the cut would split a surrogate pair (an emoji straddling
        // the boundary): a lone high surrogate is an invalid string that Npgsql's strict UTF-8
        // encoder rejects at SaveChangesAsync — the very write-path failure this clamp removes.
        var cut = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..cut];
    }
}

/// <summary>
/// Widths of the <c>AuditLog</c> columns fed by UNTRUSTED, client-controlled input — the inbound
/// <c>User-Agent</c> / <c>X-Trace-Id</c> headers and the connection's remote address. Mirrors
/// <c>ModelConfiguration</c>'s <c>HasMaxLength</c> calls; pinned equal to the EF model by
/// <c>AuditClientInputClampTests</c> so a widened column can't silently leave the boundary clamp
/// behind. (<c>Action</c> / <c>EntityType</c> are excluded on purpose: every value written to them
/// is a compile-time literal or a <c>nameof</c>, never client input.)
/// </summary>
public static class AuditColumnLengths
{
    public const int UserAgent = 500;
    public const int IpAddress = 64;
    public const int CorrelationId = 64;
}
