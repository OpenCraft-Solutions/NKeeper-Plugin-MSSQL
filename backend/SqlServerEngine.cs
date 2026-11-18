using System.Data.Common;
using Microsoft.Data.SqlClient;
using NKeeper.Abstractions;
using NKeeper.Plugins.Databases;

namespace NKeeper.Plugins.SqlServer;

/// <summary>
/// SQL Server for db-core (docs/architecture.md §7.33), ported from 1.x's <c>SqlServerConnector</c>: the connection is
/// always encrypted, and the server's certificate is trusted like an HTTPS server's. Core reads nothing of TDS, so the
/// engine reads the certificate from the server's PRELOGIN, over a TCP connection core opens (SDK 1.9), asks core about
/// it (SDK 1.7), and pins SqlClient to exactly that certificate. Core then signs in with SqlClient (SDK 1.7).
/// </summary>
public sealed class SqlServerEngine(IConnectivity connectivity, ICredentialBroker broker) : IDatabaseEngine
{
    public const string PluginId = "mssql";

    private const int ConnectTimeoutSeconds = 20;

    string IDatabaseEngine.PluginId => PluginId;

    public string Product => "SQL Server";

    public IDatabaseDialect Dialect { get; } = new SqlServerDialect();

    public async Task<IDatabaseLogin> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var handle = request.Credential ?? throw new ConnectException(ConnectFailure.NoSecret, "Choose a credential, or type one, to sign in to SQL Server.");
        var described = await broker.DescribeAsync(request.Host, request.ServiceId, handle, cancellationToken);
        if (described.Domain is { Length: > 0 })
        {
            // SqlClient's Windows logins are Kerberos or NTLM from the server's own account, never a typed password.
            throw new ConnectException(
                ConnectFailure.AuthenticationFailed,
                "NKeeper signs in to SQL Server with SQL logins. Windows (domain) logins aren't supported: use a credential without a domain.");
        }

        SqlServerCertificate certificate;
        await using (var tcp = await connectivity.OpenTcpAsync(request.Host, request.ServiceId, cancellationToken))
        {
            certificate = await SqlServerProbe.ProbeAsync(tcp.Stream, tcp.Address, tcp.Port, cancellationToken);
        }

        var identity = PresentedIdentity.Certificate(request.Host.Id, request.ServiceId, certificate.Fingerprint, certificate.Subject, certificate.NotAfterUtc);
        await connectivity.CheckIdentityAsync(request.Host, request.ServiceId, identity, certificate.PubliclyValid, cancellationToken);

        // A certificate that isn't publicly valid is pinned: SqlClient accepts exactly that one, for the name it was
        // issued to, as 1.x did. A publicly valid one is checked for the service's address, even through a tunnel.
        var address = request.Host.Services.FirstOrDefault(s => s.Id == request.ServiceId)?.Address;
        string? pinned = null;
        if (!certificate.PubliclyValid)
        {
            pinned = Path.Combine(Path.GetTempPath(), $"nkeeper-mssql-{Guid.NewGuid():N}.cer");
            await File.WriteAllBytesAsync(pinned, certificate.Raw, cancellationToken);
        }

        try
        {
            var connect = new DatabaseConnectRequest(
                SqlClientFactory.Instance,
                endpoint => new SqlConnectionStringBuilder
                {
                    DataSource = $"tcp:{endpoint.Address},{endpoint.Port}",
                    InitialCatalog = string.Empty,
                    Encrypt = SqlConnectionEncryptOption.Mandatory,
                    TrustServerCertificate = false,
                    ServerCertificate = pinned ?? string.Empty,
                    HostNameInCertificate = pinned is null ? address ?? string.Empty : certificate.DnsName,
                    ApplicationName = "NKeeper",
                    ConnectTimeout = ConnectTimeoutSeconds,
                    // Each session holds its own connections; a pool would keep logins open after it ends.
                    Pooling = false,
                    ConnectRetryCount = 0,
                },
                "User ID",
                "Password")
            {
                ConnectTimeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds + 5),
            };
            var login = await connectivity.OpenDatabaseAsync(request.Host, request.ServiceId, handle, connect, cancellationToken);
            return pinned is null ? login : new PinnedLogin(login, pinned);
        }
        catch
        {
            Delete(pinned);
            throw;
        }
    }

    private static void Delete(string? file)
    {
        if (file is null)
        {
            return;
        }

        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // A temporary copy of a certificate, which is public anyway: nothing to worry about if it stays.
        }
    }

    /// <summary>A login whose connections are pinned to a certificate in a temporary file, deleted when it closes.</summary>
    private sealed class PinnedLogin(IDatabaseLogin inner, string file) : IDatabaseLogin
    {
        public DbConnection Connection => inner.Connection;

        public Task<DbConnection> OpenAnotherAsync(CancellationToken cancellationToken = default) => inner.OpenAnotherAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            Delete(file);
        }
    }
}

/// <summary>SQL Server's query runner, object explorer, scripts and blocking manager (from 1.x's <c>SqlServerDialect</c>).</summary>
public sealed class SqlServerDialect : IDatabaseDialect
{
    private static SqlConnection Sql(DbConnection connection) => (SqlConnection)connection;

    public async Task<DatabaseServerInfo> ServerInfoAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CAST(@@SERVERNAME AS nvarchar(256)),
                   CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                   SUSER_SNAME(),
                   @@SPID
            """,
            Sql(connection));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new DatabaseServerInfo(
            reader.IsDBNull(0) ? connection.DataSource : reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetInt16(5));
    }

    public Task<SqlQueryResult> RunAsync(DbConnection connection, string script, int lineOffset, CancellationToken cancellationToken) =>
        SqlQueryRunner.RunAsync(Sql(connection), script, lineOffset, cancellationToken);

    public Task<string> CurrentDatabaseAsync(DbConnection connection, CancellationToken cancellationToken) => Task.FromResult(connection.Database);

    public async Task<IReadOnlyList<string>> DatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await SqlObjectExplorer.QueryAsync(
            Sql(connection),
            "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1 ORDER BY CASE WHEN database_id <= 4 THEN 0 ELSE 1 END, name",
            [],
            r => r.GetString(0),
            cancellationToken);

    public async Task<string> ChangeDatabaseAsync(DbConnection connection, string name, CancellationToken cancellationToken)
    {
        await connection.ChangeDatabaseAsync(name, cancellationToken);
        return connection.Database;
    }

    public Task<IReadOnlyList<SqlObjectNode>> ChildrenAsync(DbConnection connection, SqlObjectNode node, CancellationToken cancellationToken) =>
        SqlObjectExplorer.ChildrenAsync(Sql(connection), node, cancellationToken);

    public Task<string> ScriptAsync(DbConnection connection, SqlObjectNode node, string action, CancellationToken cancellationToken) =>
        SqlScripter.ScriptAsync(Sql(connection), node, action, cancellationToken);

    public Task<BlockingReport> BlockingAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken) =>
        SqlBlocking.ReadAsync(Sql(connection), now, cancellationToken);

    public Task KillAsync(DbConnection connection, int sessionId, int? serial, CancellationToken cancellationToken) =>
        SqlBlocking.KillAsync(Sql(connection), sessionId, cancellationToken);
}
