namespace Means.Core;

/// <summary>
/// Console audit event shown in the enterprise admin UI.
/// The first version records coarse actions rather than full request payloads to avoid leaking secrets.
/// </summary>
public sealed record AuditEntry(
    long Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    string Resource,
    string Status,
    string? Message);
