using FlowForge.Domain.Entities;
using FlowForge.Domain.Exceptions;
using FlowForge.Domain.Repositories;
using FlowForge.Domain.ValueObjects;
using FlowForge.Infrastructure.Caching;
using FlowForge.Infrastructure.Encryption;
using FlowForge.Infrastructure.Messaging.Outbox;
using FlowForge.Infrastructure.Persistence.Platform;
using FlowForge.Infrastructure.Repositories;
using FlowForge.Infrastructure.Triggers;
using FlowForge.Infrastructure.Triggers.Descriptors;
using FlowForge.Integration.Tests.Infrastructure;
using FlowForge.WebApi.DTOs.Requests;
using FlowForge.WebApi.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Text.Json;

namespace FlowForge.Integration.Tests.Services;

[Collection("Containers")]
public class AutomationServiceTests : IAsyncLifetime
{
    private readonly SharedContainersFixture _fixture;
    private PlatformDbContext _db = null!;
    private IDbContextTransaction _tx = null!;

    public AutomationServiceTests(SharedContainersFixture fixture)
        => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _db = _fixture.CreatePlatformDbContext();
        _tx = await _db.Database.BeginTransactionAsync();
    }

    public async Task DisposeAsync()
    {
        await _tx.RollbackAsync();
        await _db.DisposeAsync();
    }

    // ── FireWebhookAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task FireWebhook_NoSecretConfigured_AllowsWithoutProvidingSecret()
    {
        // Arrange — automation with webhook trigger that has no secret
        var (automation, redis) = await SeedWebhookAutomationAsync(secretHash: null);
        var sut = BuildService(redis);

        // Act
        var act = async () => await sut.FireWebhookAsync(automation.Id, secret: null, CancellationToken.None);

        // Assert — no exception, Redis flag is set
        await act.Should().NotThrowAsync();
        var flag = await redis.GetAsync($"trigger:webhook:{automation.Triggers[0].Id}:fired");
        flag.Should().Be("1");
    }

    [Fact]
    public async Task FireWebhook_WithCorrectSecret_SetsRedisFlag()
    {
        // Arrange
        const string plainSecret = "my-secret";
        var hash = BCrypt.Net.BCrypt.HashPassword(plainSecret);
        var (automation, redis) = await SeedWebhookAutomationAsync(secretHash: hash);
        var sut = BuildService(redis);

        // Act
        var act = async () => await sut.FireWebhookAsync(automation.Id, secret: plainSecret, CancellationToken.None);

        await act.Should().NotThrowAsync();
        var flag = await redis.GetAsync($"trigger:webhook:{automation.Triggers[0].Id}:fired");
        flag.Should().Be("1");
    }

    [Fact]
    public async Task FireWebhook_WithWrongSecret_ThrowsUnauthorizedWebhookException()
    {
        // Arrange
        var hash = BCrypt.Net.BCrypt.HashPassword("correct-secret");
        var (automation, redis) = await SeedWebhookAutomationAsync(secretHash: hash);
        var sut = BuildService(redis);

        // Act
        var act = async () => await sut.FireWebhookAsync(automation.Id, secret: "wrong-secret", CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedWebhookException>();
    }

    [Fact]
    public async Task FireWebhook_SecretConfiguredButNoneProvided_ThrowsUnauthorizedWebhookException()
    {
        // Arrange
        var hash = BCrypt.Net.BCrypt.HashPassword("some-secret");
        var (automation, redis) = await SeedWebhookAutomationAsync(secretHash: hash);
        var sut = BuildService(redis);

        // Act
        var act = async () => await sut.FireWebhookAsync(automation.Id, secret: null, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedWebhookException>();
    }

    [Fact]
    public async Task FireWebhook_WhenAutomationDisabled_ThrowsInvalidAutomationException()
    {
        // Arrange
        var (automation, redis) = await SeedWebhookAutomationAsync(secretHash: null);
        automation.Disable();
        await _db.SaveChangesAsync();
        var sut = BuildService(redis);

        // Act
        var act = async () => await sut.FireWebhookAsync(automation.Id, secret: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidAutomationException>()
            .WithMessage("*disabled*");
    }

    // ── Encryption / redaction ────────────────────────────────────────────────

    [Fact]
    public async Task CreateAutomation_SqlTrigger_StoresEncryptedConnectionStringInDb()
    {
        // Arrange
        var hostGroup = HostGroup.Create("HG-Enc", "conn-enc");
        await _db.HostGroups.AddAsync(hostGroup);
        await _db.SaveChangesAsync();

        var redis = Substitute.For<IRedisService>();
        var sut = BuildServiceWithRealEncryption(redis);

        var request = new CreateAutomationRequest(
            Name: "Encrypt Test",
            Description: null,
            HostGroupId: hostGroup.Id,
            TaskId: "run-script",
            Triggers: [new CreateTriggerRequest(
                Name: "sql-check",
                TypeId: "sql",
                ConfigJson: """{"connectionString":"Host=db;Password=s3cr3t","query":"SELECT 1"}""")],
            TriggerCondition: new TriggerConditionRequest(null, "sql-check", null));

        // Act
        await sut.CreateAsync(request, CancellationToken.None);

        // Assert — raw stored value must start with enc:v1:
        var stored = await _db.Automations
            .Include(a => a.Triggers)
            .FirstAsync(a => a.Name == "Encrypt Test");
        var storedConfig = JsonDocument.Parse(stored.Triggers[0].ConfigJson);
        storedConfig.RootElement.GetProperty("connectionString").GetString()
            .Should().StartWith("enc:v1:", "connection string must be encrypted at rest");
    }

    [Fact]
    public async Task GetAutomation_SqlTrigger_RedactsConnectionStringInResponse()
    {
        // Arrange — seed automation with encrypted connection string
        var hostGroup = HostGroup.Create("HG-Redact", "conn-redact");
        await _db.HostGroups.AddAsync(hostGroup);
        await _db.SaveChangesAsync();

        var redis = Substitute.For<IRedisService>();
        var sut = BuildServiceWithRealEncryption(redis);

        var request = new CreateAutomationRequest(
            Name: "Redact Test",
            Description: null,
            HostGroupId: hostGroup.Id,
            TaskId: "run-script",
            Triggers: [new CreateTriggerRequest(
                Name: "sql-check",
                TypeId: "sql",
                ConfigJson: """{"connectionString":"Host=db;Password=s3cr3t","query":"SELECT 1"}""")],
            TriggerCondition: new TriggerConditionRequest(null, "sql-check", null));

        var created = await sut.CreateAsync(request, CancellationToken.None);

        // Act — read back through the service
        var response = await sut.GetByIdAsync(created.Id, CancellationToken.None);

        // Assert — API response shows *** for the connection string
        var responseTrigger = response.Triggers.Single();
        var responseConfig = JsonDocument.Parse(responseTrigger.ConfigJson);
        responseConfig.RootElement.GetProperty("connectionString").GetString()
            .Should().Be("***", "sensitive fields must be redacted in API responses");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(Automation automation, IRedisService redis)> SeedWebhookAutomationAsync(string? secretHash)
    {
        var configJson = JsonSerializer.Serialize(new { SecretHash = secretHash });
        var trigger = Trigger.Create("webhook", "webhook", configJson);

        var hostGroup = HostGroup.Create("HG", "conn-webhook");
        await _db.HostGroups.AddAsync(hostGroup);

        var automation = Automation.Create("Webhook Auto", null, "task-1", hostGroup.Id, [trigger],
            new TriggerConditionNode(null, "webhook", null));
        await _db.Automations.AddAsync(automation);
        await _db.SaveChangesAsync();

        var redis = Substitute.For<IRedisService>();
        var storedValues = new Dictionary<string, string>();
        redis.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(ci =>
            {
                storedValues[ci.ArgAt<string>(0)] = ci.ArgAt<string>(1);
                return Task.CompletedTask;
            });
        redis.GetAsync(Arg.Any<string>())
            .Returns(ci => Task.FromResult<string?>(
                storedValues.TryGetValue(ci.Arg<string>(), out var v) ? v : null));

        return (automation, redis);
    }

    private AutomationService BuildService(IRedisService redis)
    {
        var automationRepo = new AutomationRepository(_db);
        var hostGroupRepo = new HostGroupRepository(_db);
        var outboxWriter = new OutboxWriter(_db);

        var registry = new TriggerTypeRegistry();
        registry.Register(new ScheduleTriggerDescriptor());
        registry.Register(new SqlTriggerDescriptor());
        registry.Register(new JobCompletedTriggerDescriptor());
        registry.Register(new WebhookTriggerDescriptor());
        registry.Register(new CustomScriptTriggerDescriptor());

        // Pass-through encryption stub — existing webhook tests don't exercise encryption
        var encryption = Substitute.For<IEncryptionService>();
        encryption.Encrypt(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        encryption.Decrypt(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        encryption.IsEncrypted(Arg.Any<string>()).Returns(false);

        return new AutomationService(automationRepo, hostGroupRepo, registry, encryption, outboxWriter, redis);
    }

    private AutomationService BuildServiceWithRealEncryption(IRedisService redis)
    {
        var automationRepo = new AutomationRepository(_db);
        var hostGroupRepo = new HostGroupRepository(_db);
        var outboxWriter = new OutboxWriter(_db);

        var registry = new TriggerTypeRegistry();
        registry.Register(new ScheduleTriggerDescriptor());
        registry.Register(new SqlTriggerDescriptor());
        registry.Register(new JobCompletedTriggerDescriptor());
        registry.Register(new WebhookTriggerDescriptor());
        registry.Register(new CustomScriptTriggerDescriptor());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>(
                    "FlowForge:EncryptionKey",
                    "dGVzdGtleXRlc3RrZXl0ZXN0a2V5dGVzdGtleXRlc3Q=")])
            .Build();
        var encryption = new AesEncryptionService(config, NullLogger<AesEncryptionService>.Instance);

        return new AutomationService(automationRepo, hostGroupRepo, registry, encryption, outboxWriter, redis);
    }
}
