using System.Text.Json;
using Microsoft.Data.SqlClient;
using NKeeper.Abstractions;
using NKeeper.Plugins.Databases;

namespace NKeeper.Plugins.SqlServer.Tests;

/// <summary>
/// The SQL Server engine against a real SQL Server (docs/architecture.md §7.33), ported from 1.x's <c>SqlServerTests</c>:
/// the certificate it reads and pins, then db-core's session on it, as the engine's session type opens it.
/// </summary>
public sealed class SqlServerTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_certificate_is_asked_about_then_pinned_and_the_driver_keeps_no_password()
    {
        sql.SkipIfUnavailable();
        var core = Core();
        var engine = new SqlServerEngine(core, new FakeBroker());

        var unknown = await Assert.ThrowsAsync<ConnectException>(() => engine.OpenAsync(Requests.Session(), Ct));
        Assert.Equal(ConnectFailure.HostKeyUnknown, unknown.Reason);
        var presented = unknown.Presented!;
        Assert.Equal((PresentedIdentityKind.TlsCertificate, "x509"), (presented.Kind, presented.Algorithm));
        Assert.Contains("SSL_Self_Signed_Fallback", presented.Subject, StringComparison.Ordinal);

        core.Trusted.Add(presented.Fingerprint);
        string pinned;
        await using (var login = await engine.OpenAsync(Requests.Session(), Ct))
        {
            var opened = new SqlConnectionStringBuilder(Assert.Single(core.Opened));
            Assert.False(opened.ContainsKey("Password") && opened.Password.Length > 0);
            pinned = opened.ServerCertificate;
            Assert.True(File.Exists(pinned));
            Assert.Equal(presented.Fingerprint, Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(pinned, Ct))).TrimEnd('='));

            await using var command = login.Connection.CreateCommand();
            command.CommandText = "SELECT encrypt_option FROM sys.dm_exec_connections WHERE session_id = @@SPID";
            Assert.Equal("TRUE", await command.ExecuteScalarAsync(Ct));
        }

        // The pinned copy goes with the login.
        Assert.False(File.Exists(pinned));
    }

    [Fact]
    public async Task A_wrong_password_and_a_Windows_login_are_refused()
    {
        sql.SkipIfUnavailable();
        var wrong = Core("not-the-password");
        var fingerprint = await TrustedFingerprintAsync(wrong);
        wrong.Trusted.Add(fingerprint);

        var refused = await Assert.ThrowsAsync<ConnectException>(() => new SqlServerEngine(wrong, new FakeBroker()).OpenAsync(Requests.Session(), Ct));
        Assert.Equal(ConnectFailure.AuthenticationFailed, refused.Reason);
        Assert.DoesNotContain("not-the-password", refused.Message, StringComparison.Ordinal);

        var windows = await Assert.ThrowsAsync<ConnectException>(() => new SqlServerEngine(Core(), new FakeBroker("CORP")).OpenAsync(Requests.Session(), Ct));
        Assert.Contains("SQL logins", windows.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_runs_batches_in_order_with_results_messages_and_errors_at_their_lines()
    {
        sql.SkipIfUnavailable();
        await using var session = await SessionAsync();
        var window = await session.WindowAsync();

        var result = await session.Channel.ReplyAsync("query", new
        {
            window,
            sql = """
                PRINT 'starting';
                SELECT 1 AS [One], N'two' AS [Two], CAST(NULL AS int) AS [Nothing], CAST(1 AS bit), 0x0A0B, CAST('2024-05-06 07:08:09.123' AS datetime) AS [When], CAST(12.50 AS decimal(9,2)) AS [Price];
                GO
                SELECT name FROM sys.databases WHERE database_id <= 2 ORDER BY database_id;
                SELECT * FROM dbo.DoesNotExist;
                GO 2
                SELECT 'after the error' AS [Still];
                """,
        });

        var output = result.GetProperty("output").EnumerateArray().ToList();
        Assert.Equal(["message", "results", "rows", "results", "rows", "error", "results", "rows", "error", "results", "rows"], output.Select(o => o.GetProperty("kind").GetString()));
        Assert.Equal("starting", output[0].GetProperty("text").GetString());
        var first = output[1];
        Assert.Equal(["One", "Two", "Nothing", "(No column name)", "(No column name)", "When", "Price"], first.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.True(first.GetProperty("columns")[0].GetProperty("numeric").GetBoolean());
        Assert.Equal(["1", "two", null, "1", "0x0A0B", "2024-05-06 07:08:09.123", "12.50"], first.GetProperty("rows")[0].EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("(1 row affected)", output[2].GetProperty("text").GetString());
        Assert.Equal(["master", "tempdb"], output[3].GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()));

        // The error is reported at its line in the script (line 5), each time the batch runs, and the script carries on.
        var error = output[5];
        Assert.Equal((208, 16, 5), (error.GetProperty("number").GetInt32(), error.GetProperty("severity").GetInt32(), error.GetProperty("line").GetInt32()));
        Assert.Equal("after the error", output[9].GetProperty("rows")[0][0].GetString());
        Assert.True(result.GetProperty("hasErrors").GetBoolean());
    }

    [Fact]
    public async Task A_window_keeps_its_state_changes_database_and_can_be_cancelled()
    {
        sql.SkipIfUnavailable();
        await using var session = await SessionAsync();
        var window = await session.WindowAsync();

        var use = await session.Channel.ReplyAsync("query", new { window, sql = "USE tempdb; CREATE TABLE #notes (id int); INSERT #notes VALUES (1), (2);" });
        Assert.Equal("tempdb", use.GetProperty("database").GetString());
        var again = await session.Channel.ReplyAsync("query", new { window, sql = "SELECT COUNT(*) FROM #notes; SELECT 1/0;", lineOffset = 10 });
        Assert.Equal("2", again.GetProperty("output")[0].GetProperty("rows")[0][0].GetString());
        Assert.Equal(11, again.GetProperty("output").EnumerateArray().First(o => o.GetProperty("kind").GetString() == "error").GetProperty("line").GetInt32());

        Assert.Equal("msdb", (await session.Channel.ReplyAsync("use", new { window, name = "msdb" })).GetProperty("name").GetString());
        Assert.Contains("master", (await session.Channel.ReplyAsync("databases")).EnumerateArray().Select(d => d.GetString()));

        var running = session.Channel.Send("query", new { window, sql = "WAITFOR DELAY '00:01:00';" });
        await Task.Delay(1000, Ct);
        await session.Channel.ReplyAsync("cancel", new { window });
        Assert.True((await session.Channel.AnswerAsync(running)).GetProperty("data").GetProperty("cancelled").GetBoolean());
        Assert.Equal("1", (await session.Channel.ReplyAsync("query", new { window, sql = "SELECT 1" })).GetProperty("output")[0].GetProperty("rows")[0][0].GetString());
    }

    [Fact]
    public async Task The_explorer_browses_like_SSMS_and_scripts_objects()
    {
        sql.SkipIfUnavailable();
        var database = $"nk_{Guid.NewGuid():N}"[..20];
        await sql.ExecuteAsync($"CREATE DATABASE [{database}]", cancellationToken: Ct);
        await sql.ExecuteAsync(
            """
            CREATE TABLE dbo.Customers (Id int IDENTITY(1,1) CONSTRAINT PK_Customers PRIMARY KEY, Name nvarchar(50) NOT NULL, Notes varchar(max) NULL);
            CREATE INDEX IX_Customers_Name ON dbo.Customers (Name);
            EXEC ('CREATE VIEW dbo.CustomerNames AS SELECT Name FROM dbo.Customers');
            EXEC ('CREATE PROCEDURE dbo.FindCustomer @name nvarchar(50), @count int OUTPUT AS SELECT @count = COUNT(*) FROM dbo.Customers WHERE Name = @name');
            """,
            database,
            Ct);
        await using var session = await SessionAsync();

        var server = await session.ExploreAsync(new { kind = "server" });
        Assert.Equal(["Databases", "Security", "Management"], server.Select(Label));
        var databases = await session.ExploreAsync(server[0]);
        Assert.Equal("System Databases", Label(databases[0]));
        Assert.Equal(["master", "model", "msdb", "tempdb"], (await session.ExploreAsync(databases[0])).Select(Label));
        var db = databases.Single(n => Label(n) == database);

        var folders = await session.ExploreAsync(db);
        Assert.Equal(["Tables", "Views", "Synonyms", "Programmability", "Security"], folders.Select(Label));
        var customers = (await session.ExploreAsync(folders[0])).Single(n => Label(n) == "dbo.Customers");
        Assert.Equal(["selectTop", "create"], customers.GetProperty("scripts").EnumerateArray().Select(s => s.GetString()));
        var tableFolders = await session.ExploreAsync(customers);
        Assert.Equal(["Columns", "Keys", "Indexes", "Triggers"], tableFolders.Select(Label));
        Assert.Empty(tableFolders[0].GetProperty("scripts").EnumerateArray());
        Assert.Equal(
            ["Id: PK, int, not null, identity", "Name: nvarchar(50), not null", "Notes: varchar(max), null"],
            (await session.ExploreAsync(tableFolders[0])).Select(n => $"{Label(n)}: {n.GetProperty("detail").GetString()}"));
        Assert.Equal(
            ["PK_Customers: Primary key, Clustered", "IX_Customers_Name: Non-Unique, Non-Clustered"],
            (await session.ExploreAsync(tableFolders[2])).Select(n => $"{Label(n)}: {n.GetProperty("detail").GetString()}"));

        var procedure = Assert.Single(await session.ExploreAsync((await session.ExploreAsync(folders[3]))[0]));
        Assert.Equal(["create", "execute"], procedure.GetProperty("scripts").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(
            ["@name: nvarchar(50), Input, No default", "@count: int, Input/Output, No default"],
            (await session.ExploreAsync(procedure)).Select(n => $"{Label(n)}: {n.GetProperty("detail").GetString()}"));

        var management = Assert.Single(await session.ExploreAsync(server[2]));
        Assert.Equal(("Blocking Manager", "blocking"), (Label(management), management.GetProperty("tool").GetString()));

        // Scripts, as SSMS writes them.
        var select = await session.ScriptAsync(customers, "selectTop");
        Assert.Contains("SELECT TOP (1000) [Id]\n      ,[Name]\n      ,[Notes]", select, StringComparison.Ordinal);
        Assert.Contains($"FROM [{database}].[dbo].[Customers]", select, StringComparison.Ordinal);
        var create = await session.ScriptAsync(customers, "create");
        Assert.Contains("CREATE TABLE [dbo].[Customers](\n\t[Id] int IDENTITY(1,1) NOT NULL,\n\t[Name] nvarchar(50) NOT NULL,", create, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED", create, StringComparison.Ordinal);
        Assert.Contains("EXECUTE @RC = [dbo].[FindCustomer] @name,\n   @count OUTPUT", await session.ScriptAsync(procedure, "execute"), StringComparison.Ordinal);

        // Odd names are quoted, never pasted in: the server says there's no such database.
        Assert.Contains("x]", await session.Channel.ErrorAsync("explore", new { node = new { kind = "tables", database = "x]; DROP DATABASE master; --" } }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_blocking_manager_shows_chains_under_their_lead_blocker_and_kills_it()
    {
        sql.SkipIfUnavailable();
        var database = $"nk_{Guid.NewGuid():N}"[..20];
        await sql.ExecuteAsync($"CREATE DATABASE [{database}]", cancellationToken: Ct);
        await sql.ExecuteAsync("CREATE TABLE dbo.Accounts (Id int PRIMARY KEY, Balance int); INSERT dbo.Accounts VALUES (1, 100);", database, Ct);
        await using var session = await SessionAsync();

        // A lead blocker: an open transaction, sleeping. Two sessions wait on it.
        await using var lead = await sql.OpenAsync(database, Ct);
        int leadId;
        await using (var spid = new SqlCommand("SELECT @@SPID", lead))
        {
            leadId = (short)(await spid.ExecuteScalarAsync(Ct))!;
        }

        await using (var begin = new SqlCommand("BEGIN TRAN; UPDATE dbo.Accounts SET Balance = 0 WHERE Id = 1;", lead))
        {
            await begin.ExecuteNonQueryAsync(Ct);
        }

        var waiters = new List<(SqlConnection Connection, Task Work)>();
        foreach (var text in new[] { "BEGIN TRAN; UPDATE dbo.Accounts SET Balance = 1 WHERE Id = 1; COMMIT;", "SELECT * FROM dbo.Accounts WITH (UPDLOCK) WHERE Id = 1;" })
        {
            var connection = await sql.OpenAsync(database, Ct);
            var command = new SqlCommand(text, connection) { CommandTimeout = 120 };
            waiters.Add((connection, command.ExecuteNonQueryAsync(Ct)));
            await Task.Delay(500, Ct);
        }

        var report = await session.Channel.ReplyAsync("blocking");
        Assert.True(report.GetProperty("canSeeAllSessions").GetBoolean());
        var chain = Assert.Single(report.GetProperty("leads").EnumerateArray());
        Assert.Equal((leadId, "sleeping", "sa", database), (chain.GetProperty("sessionId").GetInt32(), chain.GetProperty("status").GetString(), chain.GetProperty("login").GetString(), chain.GetProperty("database").GetString()));
        Assert.Equal(1, chain.GetProperty("openTransactions").GetInt32());
        Assert.Contains("UPDATE dbo.Accounts SET Balance = 0", chain.GetProperty("sql").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, chain.GetProperty("blockedVolume").GetInt32());
        Assert.Equal(2, report.GetProperty("blockedCount").GetInt32());

        await session.Channel.ReplyAsync("kill", new { sessionId = leadId });
        await Task.WhenAll(waiters.Select(w => w.Work)).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        foreach (var (connection, _) in waiters)
        {
            await connection.DisposeAsync();
        }

        Assert.Empty((await session.Channel.ReplyAsync("blocking")).GetProperty("leads").EnumerateArray());
        var killed = Assert.Single(session.Audit.Events);
        Assert.Equal("mssql.kill", killed.Action);
        Assert.StartsWith($"KILL {leadId} · sa@", killed.Detail, StringComparison.Ordinal);
    }

    private static string? Label(JsonElement node) => node.GetProperty("label").GetString();

    private FakeConnectivity Core(string password = SqlServerFixture.Password) => new(sql.Port, SqlServerFixture.UserName, password);

    /// <summary>The server's certificate, as the engine reads it before it's trusted.</summary>
    private static async Task<string> TrustedFingerprintAsync(FakeConnectivity core)
    {
        var unknown = await Assert.ThrowsAsync<ConnectException>(() => new SqlServerEngine(core, new FakeBroker()).OpenAsync(Requests.Session(), Ct));
        return unknown.Presented!.Fingerprint;
    }

    /// <summary>A database tab, as the engine's session type opens it: db-core's handler, with this engine.</summary>
    private async Task<Session> SessionAsync()
    {
        var core = Core();
        core.Trusted.Add(await TrustedFingerprintAsync(core));
        var audit = new FakeAudit();
        var handler = new DatabaseSessionHandler<SqlServerEngine>(new SqlServerEngine(core, new FakeBroker()), audit, TimeProvider.System);
        var stream = await handler.OpenAsync(Requests.Session(), Ct);
        var channel = new FakeChannel();
        return new Session(stream, channel, audit, stream.RunAsync(channel, Ct));
    }

    private sealed class Session(IStreamSession stream, FakeChannel channel, FakeAudit audit, Task running) : IAsyncDisposable
    {
        public FakeChannel Channel => channel;

        public FakeAudit Audit => audit;

        public async Task<string> WindowAsync() => (await channel.ReplyAsync("window.open")).GetProperty("window").GetString()!;

        public async Task<List<JsonElement>> ExploreAsync(object node) => [.. (await channel.ReplyAsync("explore", new { node })).EnumerateArray()];

        public async Task<string> ScriptAsync(JsonElement node, string action) => (await channel.ReplyAsync("script", new { node, action })).GetProperty("sql").GetString()!;

        public async ValueTask DisposeAsync()
        {
            channel.Close();
            await running;
            await stream.DisposeAsync();
        }
    }
}

/// <summary>The parts that need no server: GO batches (from 1.x's <c>SqlServerParsingTests</c>).</summary>
public sealed class SqlBatchesTests
{
    [Fact]
    public void Batches_split_at_GO_lines_with_their_lines_and_counts()
    {
        var batches = SqlBatches.Split("SELECT 1\r\ngo\nPRINT 'a'\n\n  GO 3 -- three times\nSELECT 2\nGO\n\nGO\n");

        Assert.Equal([("SELECT 1", 1, 1), ("PRINT 'a'", 3, 3), ("SELECT 2", 6, 1)], batches.Select(b => (b.Text, b.StartLine, b.Count)));
    }

    [Fact]
    public void GO_inside_strings_comments_and_quoted_names_is_ignored()
    {
        var script = "SELECT 'it''s\nGO\nstill a string'\n/* a /* nested */ comment\nGO\n*/\nSELECT [odd\nGO\nname]\nGO\nSELECT 2 -- GO\nGOTO label";

        var batches = SqlBatches.Split(script);

        Assert.Equal(2, batches.Count);
        Assert.StartsWith("SELECT 'it''s", batches[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("name]", batches[0].Text, StringComparison.Ordinal);
        Assert.Equal("SELECT 2 -- GO\nGOTO label", batches[1].Text);
        Assert.Equal(11, batches[1].StartLine);
    }

    [Fact]
    public void Repeat_counts_are_clamped()
    {
        Assert.Equal(SqlBatches.MaxCount, Assert.Single(SqlBatches.Split("SELECT 1\nGO 999999")).Count);
        Assert.Empty(SqlBatches.Split("\n  \nGO\n"));
    }
}
