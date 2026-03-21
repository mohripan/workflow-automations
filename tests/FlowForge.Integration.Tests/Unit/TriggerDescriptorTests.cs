using FlowForge.Domain.Triggers;
using FlowForge.Infrastructure.Triggers.Descriptors;
using FluentAssertions;

namespace FlowForge.Integration.Tests.Unit;

/// <summary>
/// Unit tests for trigger descriptor validation and metadata — no containers required.
/// </summary>
public class TriggerDescriptorTests
{
    // ── ScheduleTriggerDescriptor ─────────────────────────────────────────────

    [Fact]
    public void ScheduleDescriptor_ValidateConfig_CamelCaseJson_ReturnsNoErrors()
    {
        // This was the root cause of the Quartz scheduling bug: camelCase JSON was
        // silently ignored by case-sensitive deserialization, causing the cron expression
        // to appear null and the job to never be scheduled.
        var descriptor = new ScheduleTriggerDescriptor();
        var configJson = """{"cronExpression": "0 * * * * ?"}""";

        var errors = descriptor.ValidateConfig(configJson);

        errors.Should().BeEmpty("camelCase cronExpression must deserialize correctly");
    }

    [Fact]
    public void ScheduleDescriptor_ValidateConfig_MissingCronExpression_ReturnsError()
    {
        var descriptor = new ScheduleTriggerDescriptor();
        var configJson = """{}""";

        var errors = descriptor.ValidateConfig(configJson);

        errors.Should().ContainSingle().Which.Should().Contain("cronExpression");
    }

    [Fact]
    public void ScheduleDescriptor_ValidateConfig_InvalidCronParts_ReturnsError()
    {
        var descriptor = new ScheduleTriggerDescriptor();
        var configJson = """{"cronExpression": "* * * *"}"""; // only 4 parts, need 6

        var errors = descriptor.ValidateConfig(configJson);

        errors.Should().ContainSingle().Which.Should().Contain("cron expression");
    }

    [Fact]
    public void ScheduleDescriptor_ValidateConfig_InvalidJson_ReturnsError()
    {
        var descriptor = new ScheduleTriggerDescriptor();

        var errors = descriptor.ValidateConfig("not-json");

        errors.Should().ContainSingle().Which.Should().Contain("not valid JSON");
    }

    [Fact]
    public void ScheduleDescriptor_GetSensitiveFieldNames_ReturnsEmpty()
    {
        ITriggerTypeDescriptor descriptor = new ScheduleTriggerDescriptor();

        descriptor.GetSensitiveFieldNames().Should().BeEmpty();
    }

    // ── SqlTriggerDescriptor ──────────────────────────────────────────────────

    [Fact]
    public void SqlDescriptor_GetSensitiveFieldNames_ReturnsConnectionString()
    {
        var descriptor = new SqlTriggerDescriptor();

        descriptor.GetSensitiveFieldNames().Should().ContainSingle()
            .Which.Should().Be("connectionString");
    }

    [Fact]
    public void SqlDescriptor_ValidateConfig_ValidCamelCaseJson_ReturnsNoErrors()
    {
        var descriptor = new SqlTriggerDescriptor();
        var configJson = """{"connectionString": "Host=db;Database=erp", "query": "SELECT 1"}""";

        var errors = descriptor.ValidateConfig(configJson);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void SqlDescriptor_ValidateConfig_MissingConnectionString_ReturnsError()
    {
        var descriptor = new SqlTriggerDescriptor();
        var configJson = """{"query": "SELECT 1"}""";

        var errors = descriptor.ValidateConfig(configJson);

        errors.Should().Contain(e => e.Contains("connectionString"));
    }

    [Fact]
    public void SqlDescriptor_ValidateConfig_MissingQuery_ReturnsError()
    {
        var descriptor = new SqlTriggerDescriptor();
        var configJson = """{"connectionString": "Host=db"}""";

        var errors = descriptor.ValidateConfig(configJson);

        errors.Should().Contain(e => e.Contains("query"));
    }

    [Fact]
    public void SqlDescriptor_ValidateConfig_PollingIntervalTooLow_ReturnsError()
    {
        var descriptor = new SqlTriggerDescriptor();
        var configJson = """{"connectionString": "Host=db", "query": "SELECT 1", "pollingIntervalSeconds": 3}""";

        var errors = descriptor.ValidateConfig(configJson);

        errors.Should().Contain(e => e.Contains("pollingIntervalSeconds"));
    }
}
