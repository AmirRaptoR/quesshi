using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Quesshi.Infrastructure.Database;

internal static class DapperConventions
{
    private static int _configured;

    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0) return;
        SqlMapper.AddTypeHandler(new UtcDateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new JsonElementHandler());
        SqlMapper.AddTypeHandler(new JsonDocumentHandler());
    }

    private sealed class UtcDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            ((NpgsqlParameter)parameter).NpgsqlDbType = NpgsqlDbType.TimestampTz;
            parameter.Value = value.ToUniversalTime();
        }

        public override DateTimeOffset Parse(object value) => ((DateTimeOffset)value).ToUniversalTime();
    }

    private sealed class JsonElementHandler : SqlMapper.TypeHandler<JsonElement>
    {
        public override void SetValue(IDbDataParameter parameter, JsonElement value)
        {
            ((NpgsqlParameter)parameter).NpgsqlDbType = NpgsqlDbType.Jsonb;
            parameter.Value = value;
        }

        public override JsonElement Parse(object value) => value switch
        {
            JsonElement element => element.Clone(),
            JsonDocument document => document.RootElement.Clone(),
            string json => JsonDocument.Parse(json).RootElement.Clone(),
            _ => throw new DataException($"Cannot read {value.GetType().Name} as JSON.")
        };
    }

    private sealed class JsonDocumentHandler : SqlMapper.TypeHandler<JsonDocument>
    {
        public override void SetValue(IDbDataParameter parameter, JsonDocument? value)
        {
            ((NpgsqlParameter)parameter).NpgsqlDbType = NpgsqlDbType.Jsonb;
            parameter.Value = value is null ? DBNull.Value : value.RootElement;
        }

        public override JsonDocument Parse(object value) => value switch
        {
            JsonDocument document => JsonDocument.Parse(document.RootElement.GetRawText()),
            JsonElement element => JsonDocument.Parse(element.GetRawText()),
            string json => JsonDocument.Parse(json),
            _ => throw new DataException($"Cannot read {value.GetType().Name} as JSON.")
        };
    }
}
