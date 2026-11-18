using System.Data.Common;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using NKeeper.Abstractions;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(NKeeper.Plugins.SqlServer.Tests.SqlServerFixture))]

namespace NKeeper.Plugins.SqlServer.Tests;

/// <summary>
/// A real SQL Server 2022 (mcr.microsoft.com/mssql/server in Docker), with its self-signed certificate, as 1.x's tests
/// had. Tests are skipped, not passed, when Docker is unavailable.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string Address = "127.0.0.1";
    public const string UserName = "sa";
    public const string Password = "Sql-Test-Passw0rd!";

    private MsSqlContainer? _container;

    public string? UnavailableReason { get; private set; }

    public int Port => _container!.GetMappedPublicPort(MsSqlBuilder.MsSqlPort);

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").WithPassword(Password).Build();
            await _container.StartAsync();
        }
#pragma warning disable CA1031 // Any failure here means "no SQL Server available", which is reported as a skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UnavailableReason = $"SQL Server tests need Docker (Testcontainers). Docker was not usable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public void SkipIfUnavailable()
    {
        if (_container is null || UnavailableReason is not null)
        {
            Assert.Skip(UnavailableReason ?? "SQL Server is not available.");
        }
    }

    /// <summary>A connection of the test's own (as sa), to set things up or to hold locks.</summary>
    public async Task<SqlConnection> OpenAsync(string database = "master", CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{Address},{Port}",
            UserID = UserName,
            Password = Password,
            InitialCatalog = database,
            TrustServerCertificate = true,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task ExecuteAsync(string sql, string database = "master", CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(database, cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>
/// Core's side, as the engine sees it (docs/architecture.md §7.31): TCP to the service, the trust store's answer, and
/// a login opened with the engine's driver and the credential's password, which the engine never sees.
/// </summary>
internal sealed class FakeConnectivity(int port, string username, string password) : IConnectivity
{
    public int Port => port;

    public string Username => username;

    public string Password => password;

    /// <summary>The trust store: fingerprints trusted for the service.</summary>
    public HashSet<string> Trusted { get; } = [];

    public List<PresentedIdentity> Checked { get; } = [];

    /// <summary>The connection strings core opened, as the driver left them.</summary>
    public List<string> Opened { get; } = [];

    public async Task<ITcpConnection> OpenTcpAsync(HostContext host, Guid serviceId, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient();
        await client.ConnectAsync(SqlServerFixture.Address, port, cancellationToken);
        return new Tcp(client, port);
    }

    public Task CheckIdentityAsync(HostContext host, Guid serviceId, PresentedIdentity identity, bool publiclyValid, CancellationToken cancellationToken = default)
    {
        Checked.Add(identity);
        if (!publiclyValid && !Trusted.Contains(identity.Fingerprint))
        {
            throw new ConnectException(ConnectFailure.HostKeyUnknown, "Not trusted yet.", identity);
        }

        return Task.CompletedTask;
    }

    public async Task<IDatabaseLogin> OpenDatabaseAsync(HostContext host, Guid serviceId, CredentialHandle handle, DatabaseConnectRequest request, CancellationToken cancellationToken = default)
    {
        var login = new Login(this, request);
        await login.OpenFirstAsync(cancellationToken);
        return login;
    }

    public Task<ISshConnection> OpenSshAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IHttpConnection> OpenHttpAsync(HostContext host, Guid serviceId, bool tls, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IDesktopConnection> OpenDesktopAsync(HostContext host, Guid serviceId, CredentialHandle handle, DesktopOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private sealed class Login(FakeConnectivity core, DatabaseConnectRequest request) : IDatabaseLogin
    {
        private readonly List<DbConnection> _opened = [];

        public DbConnection Connection => _opened[0];

        public async Task OpenFirstAsync(CancellationToken cancellationToken) => await OpenAnotherAsync(cancellationToken);

        public async Task<DbConnection> OpenAnotherAsync(CancellationToken cancellationToken = default)
        {
            var builder = request.Build(new DatabaseEndpoint(SqlServerFixture.Address, core.Port));
            builder[request.UserKeyword] = core.Username;
            builder[request.PasswordKeyword] = core.Password;
            builder["Persist Security Info"] = false;
            var connection = request.Factory.CreateConnection()!;
            connection.ConnectionString = builder.ConnectionString;
            try
            {
                await connection.OpenAsync(cancellationToken);
            }
            catch (DbException ex)
            {
                await connection.DisposeAsync();
                throw new ConnectException(ConnectFailure.AuthenticationFailed, ex.Message.Replace(core.Password, "***", StringComparison.Ordinal));
            }

            core.Opened.Add(connection.ConnectionString);
            _opened.Add(connection);
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _opened)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private sealed class Tcp(TcpClient client, int port) : ITcpConnection
    {
        public Stream Stream { get; } = client.GetStream();

        public string Address => SqlServerFixture.Address;

        public int Port => port;

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The credential broker's description of the handle: a domain, or none.</summary>
internal sealed class FakeBroker(string? domain = null) : ICredentialBroker
{
    public Task<CredentialHandle> ResolveSavedAsync(HostContext host, Guid serviceId, Guid credentialId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CredentialHandle> ResolvePromptAsync(HostContext host, Guid serviceId, string promptReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CredentialDescription> DescribeAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CredentialDescription(CredentialSource.Prompt, null, SqlServerFixture.UserName, domain, true, false));
}

internal static class Requests
{
    public static readonly Guid ServiceId = Guid.NewGuid();

    public static StreamSessionRequest Session(CredentialHandle? handle = null)
    {
        var service = new HostService(ServiceId, SqlServerPlugin.ServiceType, null, 1433, SqlServerFixture.Address, JsonDocument.Parse("{}").RootElement, true);
        var host = new HostContext(Guid.NewGuid(), "sql01", null, SqlServerFixture.Address, [], HostPermission.Use) { Services = [service] };
        return new StreamSessionRequest(host, ServiceId, handle ?? Handle(), JsonDocument.Parse("{}").RootElement);
    }

    /// <summary>A handle, as the broker gives one: opaque to the plugin.</summary>
    public static CredentialHandle Handle() =>
        (CredentialHandle)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CredentialHandle));
}
