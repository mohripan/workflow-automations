using FlowForge.Domain.Entities;
using FluentAssertions;

namespace FlowForge.Domain.Tests;

public class HostGroupEntityTests
{
    [Fact]
    public void Create_ShouldInitializeProperties()
    {
        var group = HostGroup.Create("Test Group", "wf-jobs-test");

        group.Name.Should().Be("Test Group");
        group.ConnectionId.Should().Be("wf-jobs-test");
        group.RegistrationTokens.Should().BeEmpty();
        group.HasActiveTokens.Should().BeFalse();
        group.ActiveTokenCount.Should().Be(0);
    }

    [Fact]
    public void AddRegistrationToken_ShouldReturnRawTokenAndPersistEntity()
    {
        var group = HostGroup.Create("Test", "conn");

        var (token, rawToken) = group.AddRegistrationToken(TimeSpan.FromHours(24), "my-label");

        rawToken.Should().NotBeNullOrEmpty();
        token.Should().NotBeNull();
        token.Label.Should().Be("my-label");
        token.IsExpired.Should().BeFalse();
        group.RegistrationTokens.Should().HaveCount(1);
        group.HasActiveTokens.Should().BeTrue();
        group.ActiveTokenCount.Should().Be(1);
    }

    [Fact]
    public void AddRegistrationToken_ShouldSupportMultipleTokens()
    {
        var group = HostGroup.Create("Test", "conn");

        var (_, token1) = group.AddRegistrationToken(TimeSpan.FromHours(24), "token-1");
        var (_, token2) = group.AddRegistrationToken(TimeSpan.FromHours(48), "token-2");

        group.RegistrationTokens.Should().HaveCount(2);
        group.ActiveTokenCount.Should().Be(2);
        token1.Should().NotBe(token2);
    }

    [Fact]
    public void AddRegistrationToken_EachCallShouldProduceUniqueTokens()
    {
        var group = HostGroup.Create("Test", "conn");

        var (_, raw1) = group.AddRegistrationToken(TimeSpan.FromHours(1));
        var (_, raw2) = group.AddRegistrationToken(TimeSpan.FromHours(1));

        raw1.Should().NotBe(raw2);
    }

    [Fact]
    public void ValidateRegistrationToken_ShouldReturnTrueForCorrectToken()
    {
        var group = HostGroup.Create("Test", "conn");
        var (_, rawToken) = group.AddRegistrationToken(TimeSpan.FromHours(24));

        group.ValidateRegistrationToken(rawToken).Should().BeTrue();
    }

    [Fact]
    public void ValidateRegistrationToken_ShouldReturnFalseForWrongToken()
    {
        var group = HostGroup.Create("Test", "conn");
        group.AddRegistrationToken(TimeSpan.FromHours(24));

        group.ValidateRegistrationToken("wrong-token").Should().BeFalse();
    }

    [Fact]
    public void ValidateRegistrationToken_ShouldReturnFalseForEmptyToken()
    {
        var group = HostGroup.Create("Test", "conn");
        group.AddRegistrationToken(TimeSpan.FromHours(24));

        group.ValidateRegistrationToken("").Should().BeFalse();
        group.ValidateRegistrationToken(null!).Should().BeFalse();
    }

    [Fact]
    public void ValidateRegistrationToken_ShouldReturnFalseWhenNoTokensExist()
    {
        var group = HostGroup.Create("Test", "conn");

        group.ValidateRegistrationToken("any-token").Should().BeFalse();
    }

    [Fact]
    public void ValidateRegistrationToken_ShouldMatchCorrectTokenAmongMultiple()
    {
        var group = HostGroup.Create("Test", "conn");
        var (_, raw1) = group.AddRegistrationToken(TimeSpan.FromHours(24), "first");
        var (_, raw2) = group.AddRegistrationToken(TimeSpan.FromHours(24), "second");

        group.ValidateRegistrationToken(raw1).Should().BeTrue();
        group.ValidateRegistrationToken(raw2).Should().BeTrue();
        group.ValidateRegistrationToken("wrong").Should().BeFalse();
    }

    [Fact]
    public void RevokeRegistrationToken_ShouldRemoveSpecificToken()
    {
        var group = HostGroup.Create("Test", "conn");
        var (token1, raw1) = group.AddRegistrationToken(TimeSpan.FromHours(24), "keep");
        var (token2, raw2) = group.AddRegistrationToken(TimeSpan.FromHours(24), "revoke");

        var result = group.RevokeRegistrationToken(token2.Id);

        result.Should().BeTrue();
        group.RegistrationTokens.Should().HaveCount(1);
        group.ValidateRegistrationToken(raw1).Should().BeTrue();
        group.ValidateRegistrationToken(raw2).Should().BeFalse();
    }

    [Fact]
    public void RevokeRegistrationToken_ShouldReturnFalseForNonexistentToken()
    {
        var group = HostGroup.Create("Test", "conn");
        group.AddRegistrationToken(TimeSpan.FromHours(24));

        group.RevokeRegistrationToken(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void RevokeAllTokens_ShouldLeaveGroupWithNoTokens()
    {
        var group = HostGroup.Create("Test", "conn");
        var (t1, _) = group.AddRegistrationToken(TimeSpan.FromHours(24));
        var (t2, _) = group.AddRegistrationToken(TimeSpan.FromHours(24));

        group.RevokeRegistrationToken(t1.Id);
        group.RevokeRegistrationToken(t2.Id);

        group.RegistrationTokens.Should().BeEmpty();
        group.HasActiveTokens.Should().BeFalse();
    }

    [Fact]
    public void TokenHash_ShouldBe64HexChars()
    {
        var group = HostGroup.Create("Test", "conn");
        var (token, _) = group.AddRegistrationToken(TimeSpan.FromHours(1));

        token.TokenHash.Should().HaveLength(64);
        token.TokenHash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Token_ShouldHaveCorrectExpiresAt()
    {
        var before = DateTimeOffset.UtcNow;
        var group = HostGroup.Create("Test", "conn");
        var (token, _) = group.AddRegistrationToken(TimeSpan.FromHours(24));
        var after = DateTimeOffset.UtcNow;

        token.ExpiresAt.Should().BeAfter(before.AddHours(23));
        token.ExpiresAt.Should().BeBefore(after.AddHours(25));
    }

    [Fact]
    public void Update_ShouldChangeName()
    {
        var group = HostGroup.Create("Old Name", "conn");
        var originalUpdate = group.UpdatedAt;

        group.Update("New Name");

        group.Name.Should().Be("New Name");
        group.UpdatedAt.Should().BeOnOrAfter(originalUpdate);
    }

    [Fact]
    public void AddRegistrationToken_ShouldUpdateTimestamp()
    {
        var group = HostGroup.Create("Test", "conn");
        var before = group.UpdatedAt;

        group.AddRegistrationToken(TimeSpan.FromHours(1));

        group.UpdatedAt.Should().BeOnOrAfter(before);
    }
}

public class RegistrationTokenEntityTests
{
    [Fact]
    public void Create_ShouldInitializeAllProperties()
    {
        var groupId = Guid.NewGuid();
        var (token, rawToken) = RegistrationToken.Create(groupId, TimeSpan.FromHours(12), "test-label");

        token.Id.Should().NotBeEmpty();
        token.HostGroupId.Should().Be(groupId);
        token.TokenHash.Should().HaveLength(64);
        token.Label.Should().Be("test-label");
        token.IsExpired.Should().BeFalse();
        rawToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Validate_ShouldReturnTrueForCorrectToken()
    {
        var (token, rawToken) = RegistrationToken.Create(Guid.NewGuid(), TimeSpan.FromHours(1));

        token.Validate(rawToken).Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnFalseForWrongToken()
    {
        var (token, _) = RegistrationToken.Create(Guid.NewGuid(), TimeSpan.FromHours(1));

        token.Validate("wrong-token").Should().BeFalse();
    }

    [Fact]
    public void Validate_ShouldReturnFalseForEmptyInput()
    {
        var (token, _) = RegistrationToken.Create(Guid.NewGuid(), TimeSpan.FromHours(1));

        token.Validate("").Should().BeFalse();
        token.Validate(null!).Should().BeFalse();
    }

    [Fact]
    public void IsExpired_ShouldBeFalseForFutureExpiry()
    {
        var (token, _) = RegistrationToken.Create(Guid.NewGuid(), TimeSpan.FromHours(1));

        token.IsExpired.Should().BeFalse();
    }

    [Fact]
    public void Validate_SameTokenShouldProduceConsistentResults()
    {
        var (token, rawToken) = RegistrationToken.Create(Guid.NewGuid(), TimeSpan.FromHours(1));

        // Validate same token twice — should be consistent
        token.Validate(rawToken).Should().BeTrue();
        token.Validate(rawToken).Should().BeTrue();
    }

    [Fact]
    public void Create_DifferentCallsShouldProduceDifferentTokenHashes()
    {
        var (token1, _) = RegistrationToken.Create(Guid.NewGuid(), TimeSpan.FromHours(1));
        var (token2, _) = RegistrationToken.Create(Guid.NewGuid(), TimeSpan.FromHours(1));

        token1.TokenHash.Should().NotBe(token2.TokenHash);
    }
}
