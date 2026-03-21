using FlowForge.Contracts.Events;
using FlowForge.Domain.Triggers;
using FlowForge.Infrastructure.Caching;
using FlowForge.Infrastructure.Encryption;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Text.Json;

namespace FlowForge.JobAutomator.Evaluators;

public class SqlTriggerEvaluator(
    IRedisService redis,
    IEncryptionService encryption,
    ILogger<SqlTriggerEvaluator> logger) : ITriggerEvaluator
{
    public string TypeId => TriggerTypes.Sql;

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<SqlConfig>(trigger.ConfigJson, _jsonOptions);
        if (config == null || string.IsNullOrWhiteSpace(config.Query) || string.IsNullOrWhiteSpace(config.ConnectionString))
            return false;

        // Decrypt the connection string — it is stored encrypted at rest
        var connectionString = encryption.Decrypt(config.ConnectionString);

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);

            using var command = connection.CreateCommand();
            command.CommandText = config.Query;

            var result = await command.ExecuteScalarAsync(ct);

            var hash = result?.ToString() ?? "null";
            var lastHashKey = $"trigger:sql:{trigger.Id}:last-hash";
            var lastHash = await redis.GetAsync(lastHashKey);

            if (hash == lastHash) return false;

            await redis.SetAsync(lastHashKey, hash);

            if (result is bool b) return b;
            if (result is int i) return i > 0;
            if (result is long l) return l > 0;

            return result != null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SQL trigger '{TriggerName}' query failed", trigger.Name);
            return false;
        }
    }

    private record SqlConfig(string? Query, string? ConnectionString);
}
