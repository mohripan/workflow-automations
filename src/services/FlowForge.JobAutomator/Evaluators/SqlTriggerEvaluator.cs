using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Infrastructure.Caching;
using System.Text.Json;
using Npgsql;

namespace FlowForge.JobAutomator.Evaluators;

public class SqlTriggerEvaluator(IRedisService redis) : ITriggerEvaluator
{
    public TriggerType Type => TriggerType.Sql;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<SqlConfig>(trigger.ConfigJson);
        if (config == null || string.IsNullOrWhiteSpace(config.Query) || string.IsNullOrWhiteSpace(config.ConnectionString)) 
            return false;

        try
        {
            using var connection = new NpgsqlConnection(config.ConnectionString);
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
        catch (Exception)
        {
            return false;
        }
    }

    private record SqlConfig(string Query, string ConnectionString);
}
