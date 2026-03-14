using FlowForge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using FlowForge.Infrastructure.Persistence.Platform;
using System.Text.Json;

namespace FlowForge.JobAutomator.Evaluators;

public class SqlTriggerEvaluator(PlatformDbContext context) : ITriggerEvaluator
{
    public async Task<bool> EvaluateAsync(Trigger trigger, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<SqlConfig>(trigger.ConfigJson);
        if (config == null || string.IsNullOrWhiteSpace(config.Query)) return false;

        try
        {
            // Execute the query using the DB context or raw SQL.
            // For security, a real system would use a read-only connection.
            // For this mock, we check if the query returns any results.
            
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = config.Query;
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await command.Connection.OpenAsync(ct);

            var result = await command.ExecuteScalarAsync(ct);
            
            // If the query returns a non-zero count or a boolean true, trigger it.
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

    private record SqlConfig(string Query);
}
