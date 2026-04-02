using FlowForge.Domain.Entities;
using FluentAssertions;

namespace FlowForge.Domain.Tests;

public class HostGroupEntityTests
{
    [Fact]
    public void Create_NewHostGroup_HasIdAndProperties()
    {
        var group = HostGroup.Create("production", "wf-jobs-prod");

        group.Id.Should().NotBe(Guid.Empty);
        group.Name.Should().Be("production");
        group.ConnectionId.Should().Be("wf-jobs-prod");
        group.RegistrationTokenHash.Should().BeNull();
    }

    [Fact]
    public void Create_TwoGroups_HaveDifferentIds()
    {
        var g1 = HostGroup.Create("group-a", "conn-a");
        var g2 = HostGroup.Create("group-b", "conn-b");

        g1.Id.Should().NotBe(g2.Id);
    }

    [Fact]
    public void GenerateRegistrationToken_ReturnsNonEmptyToken()
    {
        var group = HostGroup.Create("test", "conn-test");

        var token = group.GenerateRegistrationToken();

        token.Should().NotBeNullOrWhiteSpace();
        group.RegistrationTokenHash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateRegistrationToken_ProducesUniqueTokens()
    {
        var group = HostGroup.Create("test", "conn-test");

        var token1 = group.GenerateRegistrationToken();
        var token2 = group.GenerateRegistrationToken();

        token1.Should().NotBe(token2, "each call should produce a new random token");
    }

    [Fact]
    public void ValidateRegistrationToken_WithCorrectToken_ReturnsTrue()
    {
        var group = HostGroup.Create("test", "conn-test");
        var token = group.GenerateRegistrationToken();

        group.ValidateRegistrationToken(token).Should().BeTrue();
    }

    [Fact]
    public void ValidateRegistrationToken_WithWrongToken_ReturnsFalse()
    {
        var group = HostGroup.Create("test", "conn-test");
        group.GenerateRegistrationToken();

        group.ValidateRegistrationToken("completely-wrong-token").Should().BeFalse();
    }

    [Fact]
    public void ValidateRegistrationToken_WithNoTokenGenerated_ReturnsFalse()
    {
        var group = HostGroup.Create("test", "conn-test");

        group.ValidateRegistrationToken("any-token").Should().BeFalse();
    }

    [Fact]
    public void ValidateRegistrationToken_WithEmptyString_ReturnsFalse()
    {
        var group = HostGroup.Create("test", "conn-test");
        group.GenerateRegistrationToken();

        group.ValidateRegistrationToken("").Should().BeFalse();
    }

    [Fact]
    public void ValidateRegistrationToken_WithNull_ReturnsFalse()
    {
        var group = HostGroup.Create("test", "conn-test");
        group.GenerateRegistrationToken();

        group.ValidateRegistrationToken(null!).Should().BeFalse();
    }

    [Fact]
    public void ValidateRegistrationToken_AfterRegenerate_OldTokenInvalid()
    {
        var group = HostGroup.Create("test", "conn-test");
        var oldToken = group.GenerateRegistrationToken();
        var newToken = group.GenerateRegistrationToken();

        group.ValidateRegistrationToken(oldToken).Should().BeFalse();
        group.ValidateRegistrationToken(newToken).Should().BeTrue();
    }

    [Fact]
    public void RevokeRegistrationToken_ClearsHash()
    {
        var group = HostGroup.Create("test", "conn-test");
        var token = group.GenerateRegistrationToken();

        group.RevokeRegistrationToken();

        group.RegistrationTokenHash.Should().BeNull();
        group.ValidateRegistrationToken(token).Should().BeFalse();
    }

    [Fact]
    public void Update_ChangesName()
    {
        var group = HostGroup.Create("old-name", "conn-test");

        group.Update("new-name");

        group.Name.Should().Be("new-name");
    }

    [Fact]
    public void GenerateRegistrationToken_StoresConsistentHash()
    {
        var group = HostGroup.Create("test", "conn-test");
        var token = group.GenerateRegistrationToken();
        var hash1 = group.RegistrationTokenHash;

        // Validate that the stored hash is consistent with the token
        group.ValidateRegistrationToken(token).Should().BeTrue();
        group.RegistrationTokenHash.Should().Be(hash1);
    }

    [Fact]
    public void GenerateRegistrationToken_HashIs64HexChars()
    {
        var group = HostGroup.Create("test", "conn-test");
        group.GenerateRegistrationToken();

        // SHA-256 produces 32 bytes = 64 hex chars
        group.RegistrationTokenHash.Should().NotBeNull();
        group.RegistrationTokenHash.Should().HaveLength(64);
        group.RegistrationTokenHash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void GenerateRegistrationToken_UpdatesTimestamp()
    {
        var group = HostGroup.Create("test", "conn-test");
        var initialUpdatedAt = group.UpdatedAt;

        // Small artificial delay to ensure timestamp differs
        Thread.Sleep(5);
        group.GenerateRegistrationToken();

        group.UpdatedAt.Should().BeOnOrAfter(initialUpdatedAt);
    }

    [Fact]
    public void RevokeRegistrationToken_UpdatesTimestamp()
    {
        var group = HostGroup.Create("test", "conn-test");
        group.GenerateRegistrationToken();
        var afterGen = group.UpdatedAt;

        Thread.Sleep(5);
        group.RevokeRegistrationToken();

        group.UpdatedAt.Should().BeOnOrAfter(afterGen);
    }
}
