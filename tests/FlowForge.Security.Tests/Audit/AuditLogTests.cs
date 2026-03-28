using FlowForge.Domain.Entities;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowForge.WebApi.DTOs.Requests;
using FlowForge.Domain.Enums;

namespace FlowForge.Security.Tests.Audit;

/// <summary>
/// Verifies that mutating API operations produce AuditLog records retrievable via GET /api/audit-logs.
/// </summary>
[Collection("SecurityTests")]
public class AuditLogTests(TestWebAppFactory factory) : IAsyncLifetime
{
    private Guid _hostGroupId;

    // ── Fixture setup ─────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        await using var db = factory.CreateDbContext();
        var hostGroup = HostGroup.Create("Audit-HG", $"audit-{Guid.NewGuid():N}");
        await db.HostGroups.AddAsync(hostGroup);
        await db.SaveChangesAsync();
        _hostGroupId = hostGroup.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAutomation_ProducesAuditLogEntry()
    {
        var adminClient = factory.CreateAdminClient();

        // Act — create automation
        var request = new CreateAutomationRequest(
            Name:             "Audited Automation",
            Description:      null,
            HostGroupId:      _hostGroupId,
            TaskId:           "run-script",
            Triggers:         [new CreateTriggerRequest("sched", "schedule", """{"cronExpression":"0 * * * * ?"}""")],
            TriggerCondition: new TriggerConditionRequest(null, "sched", null));

        var createResponse = await adminClient.PostAsJsonAsync("/api/automations", request);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created   = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var createdId = created.GetProperty("id").GetString();

        // Assert — audit log entry exists
        var logsResponse = await adminClient.GetAsync("/api/audit-logs");
        logsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var logs = await logsResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        logs.Should().NotBeNull();
        logs!.Should().Contain(e =>
            e.GetProperty("action").GetString() == "automation.created" &&
            e.GetProperty("entityId").GetString() == createdId,
            because: "creating an automation must write an audit log entry");
    }

    [Fact]
    public async Task UpdateAutomation_ProducesAuditLogEntry()
    {
        var adminClient = factory.CreateAdminClient();

        // Seed an automation first
        var createRequest = new CreateAutomationRequest(
            Name:             "To Be Updated",
            Description:      null,
            HostGroupId:      _hostGroupId,
            TaskId:           "run-script",
            Triggers:         [new CreateTriggerRequest("sched", "schedule", """{"cronExpression":"0 * * * * ?"}""")],
            TriggerCondition: new TriggerConditionRequest(null, "sched", null));

        var createResp = await adminClient.PostAsJsonAsync("/api/automations", createRequest);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created   = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var createdId = created.GetProperty("id").GetGuid();

        // Act — update it
        var updateRequest = new UpdateAutomationRequest(
            Name:             "Updated Name",
            Description:      "Now with description",
            HostGroupId:      _hostGroupId,
            TaskId:           "run-script",
            Triggers:         [new CreateTriggerRequest("sched", "schedule", """{"cronExpression":"0 * * * * ?"}""")],
            TriggerCondition: new TriggerConditionRequest(null, "sched", null));

        var updateResp = await adminClient.PutAsJsonAsync($"/api/automations/{createdId}", updateRequest);
        updateResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert
        var logsResponse = await adminClient.GetAsync($"/api/audit-logs?entityId={createdId}");
        logsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var logs = await logsResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        logs!.Should().Contain(e =>
            e.GetProperty("action").GetString() == "automation.updated",
            because: "updating an automation must write an audit log entry");
    }

    [Fact]
    public async Task DeleteAutomation_ProducesAuditLogEntry()
    {
        var adminClient = factory.CreateAdminClient();

        // Seed
        var createRequest = new CreateAutomationRequest(
            Name:             "To Be Deleted",
            Description:      null,
            HostGroupId:      _hostGroupId,
            TaskId:           "run-script",
            Triggers:         [new CreateTriggerRequest("sched", "schedule", """{"cronExpression":"0 * * * * ?"}""")],
            TriggerCondition: new TriggerConditionRequest(null, "sched", null));

        var createResp = await adminClient.PostAsJsonAsync("/api/automations", createRequest);
        var created    = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var createdId  = created.GetProperty("id").GetGuid();

        // Act
        var deleteResp = await adminClient.DeleteAsync($"/api/automations/{createdId}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert
        var logsResponse = await adminClient.GetAsync($"/api/audit-logs?entityId={createdId}");
        var logs = await logsResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        logs!.Should().Contain(e =>
            e.GetProperty("action").GetString() == "automation.deleted",
            because: "deleting an automation must write an audit log entry");
    }

    [Fact]
    public async Task AuditLogs_AsViewer_Returns403()
    {
        var response = await factory.CreateViewerClient().GetAsync("/api/audit-logs");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AuditLogs_AsAdmin_Returns200()
    {
        var response = await factory.CreateAdminClient().GetAsync("/api/audit-logs");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
