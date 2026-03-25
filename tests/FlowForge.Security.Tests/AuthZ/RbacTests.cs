using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using FlowForge.WebApi.DTOs.Requests;
using FlowForge.Domain.Enums;

namespace FlowForge.Security.Tests.AuthZ;

/// <summary>
/// Verifies role-based authorization: wrong role → 403, correct role → non-403.
/// </summary>
[Collection("SecurityTests")]
public class RbacTests(TestWebAppFactory factory)
{
    private static readonly Guid AnyId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // ── AdminOnly endpoints ───────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAutomation_AsViewer_Returns403()
    {
        var response = await factory.CreateViewerClient()
            .DeleteAsync($"/api/automations/{AnyId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteAutomation_AsOperator_Returns403()
    {
        var response = await factory.CreateOperatorClient()
            .DeleteAsync($"/api/automations/{AnyId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetDlq_AsViewer_Returns403()
    {
        var response = await factory.CreateViewerClient()
            .GetAsync("/api/dlq");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetDlq_AsOperator_Returns403()
    {
        var response = await factory.CreateOperatorClient()
            .GetAsync("/api/dlq");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetAuditLogs_AsViewer_Returns403()
    {
        var response = await factory.CreateViewerClient()
            .GetAsync("/api/audit-logs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetAuditLogs_AsOperator_Returns403()
    {
        var response = await factory.CreateOperatorClient()
            .GetAsync("/api/audit-logs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── OperatorOrAbove endpoints ─────────────────────────────────────────────

    [Fact]
    public async Task CreateAutomation_AsViewer_Returns403()
    {
        var request = new CreateAutomationRequest(
            "Test", null, Guid.NewGuid(), "run-script",
            [], new TriggerConditionRequest(ConditionOperator.And, null, []));

        var response = await factory.CreateViewerClient()
            .PostAsJsonAsync("/api/automations", request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EnableAutomation_AsViewer_Returns403()
    {
        var response = await factory.CreateViewerClient()
            .PutAsync($"/api/automations/{AnyId}/enable", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DisableAutomation_AsViewer_Returns403()
    {
        var response = await factory.CreateViewerClient()
            .PutAsync($"/api/automations/{AnyId}/disable", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── ViewerOrAbove endpoints — all roles must be admitted ──────────────────

    [Theory]
    [InlineData("admin")]
    [InlineData("operator")]
    [InlineData("viewer")]
    public async Task GetAutomations_WithAnyRole_Returns200(string role)
    {
        var token = role switch
        {
            "admin"    => TestTokenFactory.ForAdmin(),
            "operator" => TestTokenFactory.ForOperator(),
            _          => TestTokenFactory.ForViewer(),
        };

        var response = await factory.CreateClientWithToken(token)
            .GetAsync("/api/automations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── InternalService endpoint (M2M only) ───────────────────────────────────

    [Theory]
    [InlineData("admin")]
    [InlineData("operator")]
    [InlineData("viewer")]
    public async Task GetSnapshots_WithAnyUserRole_Returns403(string role)
    {
        // /api/automations/snapshots requires azp="flowforge-jobautomator", not a realm role
        var token = role switch
        {
            "admin"    => TestTokenFactory.ForAdmin(),
            "operator" => TestTokenFactory.ForOperator(),
            _          => TestTokenFactory.ForViewer(),
        };

        var response = await factory.CreateClientWithToken(token)
            .GetAsync("/api/automations/snapshots");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
