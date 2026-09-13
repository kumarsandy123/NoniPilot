using NoniPilot.Domain.Models;

namespace NoniPilot.Domain.Interfaces;

/// <summary>Persists the tamper-aware audit trail (section 9/18): every command, plan, action and outcome.</summary>
public interface IAuditService
{
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEvent>> QueryAsync(string? taskId = null, int limit = 200, CancellationToken cancellationToken = default);
}
