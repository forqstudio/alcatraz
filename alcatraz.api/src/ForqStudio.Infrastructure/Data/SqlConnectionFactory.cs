using System.Data;
using ForqStudio.Application.Abstractions.Data;
using Npgsql;

namespace ForqStudio.Infrastructure.Data;

internal sealed class SqlConnectionFactory(string connectionString) : ISqlConnectionFactory
{
    private readonly string connectionString = connectionString;

    public IDbConnection CreateConnection()
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        return connection;
    }
}