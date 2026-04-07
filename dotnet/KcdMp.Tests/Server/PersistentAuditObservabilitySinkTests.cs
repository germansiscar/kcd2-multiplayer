using System.Text.Json;
using KcdMp.Server.Audit;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Server;

public sealed class PersistentAuditObservabilitySinkTests
{
    [Fact]
    public void Emit_PersistsSelectedEvent_WithSensitivePayloadRedacted()
    {
        var root = CreateTempDir();
        try
        {
            var sink = new PersistentAuditObservabilitySink(
                new JsonPersistenceOptions { BasePath = root, Environment = JsonPersistenceEnvironment.Development },
                new ServerAuditOptions { Enabled = true, RetentionDays = 30, WriteIndented = true });
            var occurredAt = new DateTimeOffset(2026, 4, 7, 10, 0, 0, TimeSpan.Zero);

            sink.Emit(new ServerObservableEvent(
                Type: ServerObservableEventType.AccessDecisionDenied,
                Component: ServerObservableComponent.Session,
                Severity: ServerObservableSeverity.Warning,
                OccurredAtUtc: occurredAt,
                SessionId: Guid.NewGuid(),
                IdentityId: "pid_henry",
                Message: "Access denied.",
                Payload: new Dictionary<string, object?>
                {
                    ["reason"] = "DeniedPendingIdentityInWhitelistMode",
                    ["password"] = "secret123",
                }));

            var partitionPath = Path.Combine(root, JsonPersistenceDomains.Audit, "2026-04-07.json");
            Assert.True(File.Exists(partitionPath));

            var json = File.ReadAllText(partitionPath);
            var records = JsonSerializer.Deserialize<List<ServerAuditEventRecord>>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

            var record = Assert.Single(records!);
            Assert.Equal(ServerAuditCategory.Access, record.Category);
            Assert.Equal(ServerAuditResult.Fail, record.Result);
            Assert.Equal("pid_henry", record.IdentityId);
            Assert.Equal("[REDACTED]", record.Payload!["password"]?.ToString());
            Assert.Equal("DeniedPendingIdentityInWhitelistMode", record.Payload["reason"]?.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Emit_IgnoresNonAuditableEvent()
    {
        var root = CreateTempDir();
        try
        {
            var sink = new PersistentAuditObservabilitySink(
                new JsonPersistenceOptions { BasePath = root },
                new ServerAuditOptions { Enabled = true, RetentionDays = 30 });

            sink.Emit(new ServerObservableEvent(
                Type: ServerObservableEventType.ServerStarted,
                Component: ServerObservableComponent.ServerLifecycle,
                Severity: ServerObservableSeverity.Information,
                OccurredAtUtc: new DateTimeOffset(2026, 4, 7, 10, 0, 0, TimeSpan.Zero),
                Message: "server started"));

            var auditDir = Path.Combine(root, JsonPersistenceDomains.Audit);
            var partitions = Directory.EnumerateFiles(auditDir, "*.json", SearchOption.TopDirectoryOnly).ToArray();
            Assert.Empty(partitions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Emit_AppliesRetention_ByDatePartition()
    {
        var root = CreateTempDir();
        try
        {
            var auditDir = Path.Combine(root, JsonPersistenceDomains.Audit);
            Directory.CreateDirectory(auditDir);
            var oldPath = Path.Combine(auditDir, "2026-01-01.json");
            File.WriteAllText(oldPath, "[]");

            var sink = new PersistentAuditObservabilitySink(
                new JsonPersistenceOptions { BasePath = root },
                new ServerAuditOptions { Enabled = true, RetentionDays = 7 });

            sink.Emit(new ServerObservableEvent(
                Type: ServerObservableEventType.IdentityCreated,
                Component: ServerObservableComponent.Persistence,
                Severity: ServerObservableSeverity.Information,
                OccurredAtUtc: new DateTimeOffset(2026, 4, 7, 10, 0, 0, TimeSpan.Zero),
                IdentityId: "pid_new",
                Message: "identity created"));

            Assert.False(File.Exists(oldPath));
            Assert.True(File.Exists(Path.Combine(auditDir, "2026-04-07.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Emit_BanApplied_IsAuditedAsAccessCategory()
    {
        var root = CreateTempDir();
        try
        {
            var sink = new PersistentAuditObservabilitySink(
                new JsonPersistenceOptions { BasePath = root, Environment = JsonPersistenceEnvironment.Development },
                new ServerAuditOptions { Enabled = true, RetentionDays = 30, WriteIndented = true });
            var occurredAt = new DateTimeOffset(2026, 4, 7, 12, 0, 0, TimeSpan.Zero);

            sink.Emit(new ServerObservableEvent(
                Type: ServerObservableEventType.BanApplied,
                Component: ServerObservableComponent.Persistence,
                Severity: ServerObservableSeverity.Warning,
                OccurredAtUtc: occurredAt,
                IdentityId: "pid_banned",
                Message: "Identity ban applied.",
                Payload: new Dictionary<string, object?>
                {
                    ["actor_id"] = "admin_1",
                    ["ban_id"] = "ban_123",
                }));

            var partitionPath = Path.Combine(root, JsonPersistenceDomains.Audit, "2026-04-07.json");
            Assert.True(File.Exists(partitionPath));

            var json = File.ReadAllText(partitionPath);
            var records = JsonSerializer.Deserialize<List<ServerAuditEventRecord>>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

            var record = Assert.Single(records!);
            Assert.Equal(ServerAuditCategory.Access, record.Category);
            Assert.Equal(ServerAuditResult.Ok, record.Result);
            Assert.Equal("pid_banned", record.IdentityId);
            Assert.Equal("admin_1", record.ActorId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_audit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}

