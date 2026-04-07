using System.Text.Json;
using KcdMp.Shared.Protocol;

namespace KcdMp.Client;

public enum ClientAdaptationPhase
{
    Pending = 0,
    Partial = 1,
    Complete = 2,
    Error = 3,
}

public sealed record ClientDerivedStateSnapshot(
    byte? TransportClientId,
    string? SessionId,
    string? IdentityId,
    string? CharacterId,
    string? Readiness,
    string? AccessMode,
    string? IdentityRole,
    string? IdentityStatus,
    string? CharacterStatus,
    bool IsAccessDenied,
    bool IsBanned,
    bool IsKicked,
    bool IsInvalidated,
    string? InvalidationReason,
    ClientAdaptationPhase AdaptationPhase,
    IReadOnlyDictionary<ProjectionDomain, ProjectionApplyStatus> ProjectionStates);

public sealed class ClientDerivedState
{
    private readonly object _lock = new();
    private readonly Dictionary<ProjectionDomain, ProjectionApplyStatus> _projectionStates = [];

    private byte? _transportClientId;
    private string? _sessionId;
    private string? _identityId;
    private string? _characterId;
    private string? _readiness;
    private string? _accessMode;
    private string? _identityRole;
    private string? _identityStatus;
    private string? _characterStatus;
    private bool _isAccessDenied;
    private bool _isBanned;
    private bool _isKicked;
    private bool _isInvalidated;
    private string? _invalidationReason;

    public void BeginNewCycle()
    {
        lock (_lock)
        {
            _transportClientId = null;
            _sessionId = null;
            _identityId = null;
            _characterId = null;
            _readiness = null;
            _accessMode = null;
            _identityRole = null;
            _identityStatus = null;
            _characterStatus = null;
            _isAccessDenied = false;
            _isBanned = false;
            _isKicked = false;
            _isInvalidated = false;
            _invalidationReason = null;
            _projectionStates.Clear();
        }
    }

    public void SetTransportClientId(byte transportClientId)
    {
        lock (_lock)
        {
            _transportClientId = transportClientId;
        }
    }

    public void MarkProjectionStarted(ProjectionDomain domain)
    {
        lock (_lock)
        {
            _projectionStates[domain] = ProjectionApplyStatus.Started;
        }
    }

    public void MarkProjectionResult(ProjectionDomain domain, ProjectionApplyStatus status)
    {
        lock (_lock)
        {
            _projectionStates[domain] = status;
        }
    }

    public void ApplySessionCharacterProjection(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        lock (_lock)
        {
            _sessionId = ReadOptionalString(root, "sessionId") ?? _sessionId;
            _identityId = ReadOptionalString(root, "identityId") ?? _identityId;
            _characterId = ReadOptionalString(root, "characterId") ?? _characterId;
            _readiness = ReadOptionalString(root, "readiness") ?? _readiness;
        }
    }

    public void ApplyAdministrativeProjection(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        lock (_lock)
        {
            _accessMode = ReadOptionalString(root, "accessMode") ?? _accessMode;
            _identityRole = ReadOptionalString(root, "identityRole") ?? _identityRole;
            _identityStatus = ReadOptionalString(root, "identityStatus") ?? _identityStatus;
            _characterStatus = ReadOptionalString(root, "characterStatus") ?? _characterStatus;
            _isAccessDenied = ReadOptionalBool(root, "accessDenied") ?? _isAccessDenied;
            _isBanned = ReadOptionalBool(root, "isBanned") ?? _isBanned;
            _isKicked = ReadOptionalBool(root, "kicked") ?? _isKicked;

            if (ShouldInvalidate(root, out var reason))
            {
                _isInvalidated = true;
                _invalidationReason = reason;
            }
        }
    }

    public void MarkInvalidated(string reason)
    {
        lock (_lock)
        {
            _isInvalidated = true;
            _invalidationReason = string.IsNullOrWhiteSpace(reason) ? "Session invalidated by server." : reason.Trim();
        }
    }

    public ClientDerivedStateSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new ClientDerivedStateSnapshot(
                _transportClientId,
                _sessionId,
                _identityId,
                _characterId,
                _readiness,
                _accessMode,
                _identityRole,
                _identityStatus,
                _characterStatus,
                _isAccessDenied,
                _isBanned,
                _isKicked,
                _isInvalidated,
                _invalidationReason,
                ResolveAdaptationPhaseUnsafe(),
                new Dictionary<ProjectionDomain, ProjectionApplyStatus>(_projectionStates));
        }
    }

    private ClientAdaptationPhase ResolveAdaptationPhaseUnsafe()
    {
        if (_projectionStates.Count == 0)
            return ClientAdaptationPhase.Pending;

        if (_projectionStates.Values.Any(x => x is ProjectionApplyStatus.Failed))
            return ClientAdaptationPhase.Error;

        if (_projectionStates.Values.Any(x => x is ProjectionApplyStatus.PartiallyApplied or ProjectionApplyStatus.NotApplied))
            return ClientAdaptationPhase.Partial;

        if (_projectionStates.Values.All(x => x is ProjectionApplyStatus.Applied))
            return ClientAdaptationPhase.Complete;

        return ClientAdaptationPhase.Pending;
    }

    private static bool ShouldInvalidate(JsonElement root, out string reason)
    {
        var explicitMessage = ReadOptionalString(root, "message");
        if ((ReadOptionalBool(root, "accessDenied") ?? false) || (ReadOptionalBool(root, "isBanned") ?? false))
        {
            reason = explicitMessage ?? "Access denied by server.";
            return true;
        }

        if (ReadOptionalBool(root, "kicked") ?? false)
        {
            reason = explicitMessage ?? "You were kicked from the server.";
            return true;
        }

        var identityStatus = ReadOptionalString(root, "identityStatus");
        if (string.Equals(identityStatus, "Blocked", StringComparison.OrdinalIgnoreCase))
        {
            reason = explicitMessage ?? "Identity blocked by server administration.";
            return true;
        }

        var characterStatus = ReadOptionalString(root, "characterStatus");
        if (string.Equals(characterStatus, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            reason = explicitMessage ?? "Selected character is disabled.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return null;

        var value = node.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool? ReadOptionalBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node) || (node.ValueKind != JsonValueKind.True && node.ValueKind != JsonValueKind.False))
            return null;

        return node.GetBoolean();
    }
}
