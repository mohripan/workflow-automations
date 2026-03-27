using FlowForge.Domain.Entities;
using FluentAssertions;

namespace FlowForge.Domain.Tests;

public class TriggerEntityTests
{
    [Fact]
    public void Create_WithValidInputs_SetsAllProperties()
    {
        const string configJson = """{"expression":"0 0 * * *"}""";

        var trigger = Trigger.Create("daily", "schedule", configJson);

        trigger.Name.Should().Be("daily");
        trigger.TypeId.Should().Be("schedule");
        trigger.ConfigJson.Should().Be(configJson);
        trigger.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Create_TwoCalls_ProduceDifferentIds()
    {
        var t1 = Trigger.Create("t1", "schedule", "{}");
        var t2 = Trigger.Create("t2", "schedule", "{}");

        t1.Id.Should().NotBe(t2.Id);
    }

    [Fact]
    public void Create_WebhookType_StoresCorrectTypeId()
    {
        var trigger = Trigger.Create("wh", "webhook", """{"Secret":null}""");

        trigger.TypeId.Should().Be("webhook");
    }

    [Fact]
    public void Create_SqlType_StoresConfigJsonVerbatim()
    {
        const string configJson = """{"connectionString":"Host=db","query":"SELECT COUNT(*) FROM changes"}""";

        var trigger = Trigger.Create("sql-check", "sql", configJson);

        trigger.ConfigJson.Should().Be(configJson);
    }

    [Fact]
    public void Create_CustomScriptType_StoresScriptContent()
    {
        const string configJson = """{"scriptContent":"print('true')","pollingIntervalSeconds":30}""";

        var trigger = Trigger.Create("py-check", "custom-script", configJson);

        trigger.TypeId.Should().Be("custom-script");
        trigger.ConfigJson.Should().Be(configJson);
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("sql")]
    [InlineData("webhook")]
    [InlineData("job-completed")]
    [InlineData("custom-script")]
    public void Create_AnyBuiltInTypeId_IsStoredExactly(string typeId)
    {
        var trigger = Trigger.Create("t", typeId, "{}");

        trigger.TypeId.Should().Be(typeId);
    }
}
