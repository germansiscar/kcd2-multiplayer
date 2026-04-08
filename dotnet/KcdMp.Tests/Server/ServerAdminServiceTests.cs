using System.Collections.Concurrent;
using System.Text.Json;
using KcdMp.Server.AccessControl;
using KcdMp.Server.Admin;
using KcdMp.Server.Audit;
using KcdMp.Server.Bans;
using KcdMp.Server.Characters;
using KcdMp.Server.Crime;
using KcdMp.Server.Identity;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using KcdMp.Server.Sessions;

namespace KcdMp.Tests.Server;

public sealed class ServerAdminServiceTests
{
    [Fact]
    public async Task ApproveWhitelistIdentityAsync_RequiresAdminRole()
    {
        var root = CreateTempDir();
        try
        {
            var (admin, identities, _, service, _) = await CreateAdminServiceAsync(root);
            await identities.TrySetRoleAsync(admin.InternalId, PlayerIdentityRole.Player);

            var target = (await identities.ResolveOrCreateAsync(
                new PlayerIdentityClaim("Pending", "pending_token", null),
                ServerAccessMode.Whitelist)).Identity!;
            var result = await service.ApproveWhitelistIdentityAsync(admin.InternalId, target.InternalId);

            Assert.False(result.Success);
            Assert.Equal("Operation requires active admin role.", result.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApproveWhitelistIdentityAsync_ChangesStatusToActive()
    {
        var root = CreateTempDir();
        try
        {
            var (admin, identities, _, service, sink) = await CreateAdminServiceAsync(root);
            var target = (await identities.ResolveOrCreateAsync(
                new PlayerIdentityClaim("Pending", "pending_token", null),
                ServerAccessMode.Whitelist)).Identity!;

            var listBefore = await service.ListPendingWhitelistAsync(admin.InternalId);
            var approve = await service.ApproveWhitelistIdentityAsync(admin.InternalId, target.InternalId);
            var refreshed = await identities.GetByInternalIdAsync(target.InternalId);
            var listAfter = await service.ListPendingWhitelistAsync(admin.InternalId);

            Assert.True(listBefore.Success);
            Assert.Contains(listBefore.Value!, x => x.InternalId == target.InternalId);
            Assert.True(approve.Success);
            Assert.NotNull(refreshed);
            Assert.Equal(PlayerIdentityStatus.Active, refreshed!.Status);
            Assert.True(listAfter.Success);
            Assert.DoesNotContain(listAfter.Value!, x => x.InternalId == target.InternalId);
            Assert.Contains(sink.Events, x => x.Type == ServerObservableEventType.WhitelistApproved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisableCharacterAsync_KicksActiveCharacterSession()
    {
        var root = CreateTempDir();
        try
        {
            var (admin, identities, characters, service, _) = await CreateAdminServiceAsync(root);
            var owner = (await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Owner", "owner_token", null))).Identity!;
            var created = await characters.CreateAsync(
                owner.InternalId,
                new CharacterCreateRequest("John", "Smith", "henry"));
            Assert.True(created.Created);
            var characterId = created.Character!.InternalId;

            var sessionId = Guid.NewGuid();
            var sessions = _sharedSessions;
            lock (sessions)
            {
                sessions.Clear();
                sessions.Add(new ServerSessionRecord
                {
                    SessionId = sessionId,
                    IdentityId = owner.InternalId,
                    CharacterId = characterId,
                    State = ServerSessionState.Active,
                    AuthState = ServerSessionAuthState.Accepted,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    LastActivityAtUtc = DateTimeOffset.UtcNow,
                });
            }

            var disable = await service.DisableCharacterAsync(admin.InternalId, characterId);
            var inspect = await service.InspectSessionAsync(admin.InternalId, sessionId);

            Assert.True(disable.Success);
            Assert.False(inspect.Success);
            var updated = await characters.GetByInternalIdAsync(characterId);
            Assert.NotNull(updated);
            Assert.Equal(CharacterProfileStatus.Disabled, updated!.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            lock (_sharedSessions)
                _sharedSessions.Clear();
        }
    }

    [Fact]
    public async Task QueryAuditAsync_FiltersByIdentityAndEventType()
    {
        var root = CreateTempDir();
        try
        {
            var (admin, _, _, service, _) = await CreateAdminServiceAsync(root);
            var auditDir = Path.Combine(root, JsonPersistenceDomains.Audit);
            Directory.CreateDirectory(auditDir);

            var records = new List<ServerAuditEventRecord>
            {
                new(
                    AuditEventId: "a1",
                    TimestampUtc: new DateTimeOffset(2026, 4, 7, 12, 0, 0, TimeSpan.Zero),
                    EventType: "BanApplied",
                    Category: ServerAuditCategory.Access,
                    Severity: ServerObservableSeverity.Warning,
                    Result: ServerAuditResult.Ok,
                    IdentityId: "pid_target",
                    Message: "ban applied"),
                new(
                    AuditEventId: "a2",
                    TimestampUtc: new DateTimeOffset(2026, 4, 7, 12, 5, 0, TimeSpan.Zero),
                    EventType: "IdentityCreated",
                    Category: ServerAuditCategory.Identity,
                    Severity: ServerObservableSeverity.Information,
                    Result: ServerAuditResult.Ok,
                    IdentityId: "pid_other",
                    Message: "identity created"),
            };

            await File.WriteAllTextAsync(
                Path.Combine(auditDir, "2026-04-07.json"),
                JsonSerializer.Serialize(records, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

            var query = await service.QueryAuditAsync(
                admin.InternalId,
                new AdminAuditQuery(
                    IdentityId: "pid_target",
                    EventType: "BanApplied",
                    FromUtc: new DateTimeOffset(2026, 4, 7, 0, 0, 0, TimeSpan.Zero),
                    ToUtc: new DateTimeOffset(2026, 4, 7, 23, 59, 59, TimeSpan.Zero)));

            Assert.True(query.Success);
            Assert.NotNull(query.Value);
            Assert.Equal(1, query.Value!.TotalMatched);
            Assert.Equal("a1", Assert.Single(query.Value.Events).AuditEventId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MarkCrimeAndClearCrimeStateAsync_WorkForAdmin()
    {
        var root = CreateTempDir();
        try
        {
            var (admin, _, _, service, _) = await CreateAdminServiceAsync(root);

            var mark = await service.MarkCrimeAsync(
                admin.InternalId,
                new AdminCrimeMarkRequest(
                    IdentityId: "pid_marked",
                    CharacterId: "cid_marked",
                    CrimeType: CrimeType.ManualStaffMark,
                    TargetKind: CrimeTargetKind.None,
                    Reason: "manual_case"));

            Assert.True(mark.Success);
            Assert.NotNull(mark.Value);
            Assert.Equal(CharacterCrimeStatus.Wanted, mark.Value!.Status);

            var clear = await service.ClearCrimeStateAsync(
                admin.InternalId,
                identityId: "pid_marked",
                characterId: "cid_marked",
                reason: "resolved");

            Assert.True(clear.Success);
            Assert.NotNull(clear.Value);
            Assert.Equal(CharacterCrimeStatus.Clean, clear.Value!.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly List<ServerSessionRecord> _sharedSessions = [];

    private static async Task<(PlayerIdentityRecord admin, IPlayerIdentityService identities, ICharacterProfileService characters, IServerAdminService adminService, InMemoryObservabilitySink sink)> CreateAdminServiceAsync(string root)
    {
        var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
        var sink = new InMemoryObservabilitySink();
        var identities = new PlayerIdentityService(store, sink);
        var characters = new CharacterProfileService(store, identities, sink);
        var bans = new IdentityBanService(store, sink);
        var crime = new CrimeLawService(store, sink);

        var admin = (await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Admin", "admin_token", null))).Identity!;
        await identities.TrySetRoleAsync(admin.InternalId, PlayerIdentityRole.Admin);
        await identities.TrySetStatusAsync(admin.InternalId, PlayerIdentityStatus.Active);

        var service = new ServerAdminService(
            identities,
            characters,
            bans,
            crime,
            sink,
            listSessions: () =>
            {
                lock (_sharedSessions)
                    return _sharedSessions.Select(x => x.Clone()).ToArray();
            },
            kickSession: sessionId =>
            {
                lock (_sharedSessions)
                {
                    var removed = _sharedSessions.RemoveAll(x => x.SessionId == sessionId);
                    return removed > 0;
                }
            },
            applyBan: (request, ct) => bans.ApplyBanAsync(request, null, ct),
            revokeBan: (request, ct) => bans.RevokeActiveBanAsync(request, null, ct),
            persistenceOptions: new JsonPersistenceOptions { BasePath = root });

        return (admin, identities, characters, service, sink);
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_admin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class InMemoryObservabilitySink : IServerObservabilitySink
    {
        private readonly ConcurrentQueue<ServerObservableEvent> _events = new();
        public IReadOnlyCollection<ServerObservableEvent> Events => _events.ToArray();

        public void Emit(ServerObservableEvent observableEvent)
        {
            _events.Enqueue(observableEvent);
        }
    }
}
