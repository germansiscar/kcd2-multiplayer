namespace KcdMp.Server.Observability;

public enum ServerObservableSeverity
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
}

public enum ServerObservableComponent
{
    Backend = 0,
    Session = 1,
    Networking = 2,
    Persistence = 3,
    ServerLifecycle = 4,
    ClientIntegration = 5,
}

public enum ServerObservableEventType
{
    ServerStarted = 0,
    ServerStopped = 1,
    SessionCreated = 2,
    SessionClosed = 3,
    SessionTimeout = 4,
    AuthenticationAccepted = 5,
    AuthenticationRejected = 6,
    AssociationPending = 7,
    BackendError = 8,
    PersistenceLoadCompleted = 9,
    PersistenceLoadFailed = 10,
    PersistenceSaveCompleted = 11,
    PersistenceSaveFailed = 12,
    IdentityResolved = 13,
    IdentityCreated = 14,
    IdentityAccessDenied = 15,
    IdentityStatusChanged = 16,
}

public sealed record ServerObservableEvent(
    ServerObservableEventType Type,
    ServerObservableComponent Component,
    ServerObservableSeverity Severity,
    DateTimeOffset OccurredAtUtc,
    Guid? SessionId = null,
    string? IdentityId = null,
    string? CharacterId = null,
    string? Message = null,
    IReadOnlyDictionary<string, object?>? Payload = null);

