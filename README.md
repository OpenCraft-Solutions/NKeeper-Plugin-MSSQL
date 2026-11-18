# NKeeper SQL Server plugin

The official `mssql` plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0: SQL Server, like a small SSMS. It lives in a repository of its own and is released on its own, so releasing it isn't a release of NKeeper (NKeeper's `docs/architecture.md` §9). Its design is §7.33 there.

## What it does

It's a database engine for `db-core` (§7.32), which it depends on: db-core runs the session and draws the tab, and this plugin brings SQL Server's driver and SQL. It needs SDK 1.9 (`>=1.9.0 <2.0.0`) and db-core 1.x.

It contributes the **`mssql` service type** ("SQL Server", port 1433, health by TCP), and its database tab (`mssql.query`), ported from 1.x:
- **Signing in.** The connection is always encrypted. The plugin reads the server's certificate from its PRELOGIN, over a TCP connection core opens (SDK 1.9), and asks core about it, as for an HTTPS certificate (SDK 1.7):
  - a publicly valid certificate is accepted;
  - any other is shown to the user once, and SqlClient is then pinned to exactly that certificate.

  Core then signs in with SqlClient and the credential's password, which the plugin never sees (SDK 1.7). SQL logins only: a credential with a domain is refused, as in 1.x.
- **Query windows**, each on a connection of its own: `GO` batches (with counts), results, row counts, `PRINT` messages and errors at their line in the editor, cancel, and the database list.
- **The object explorer**, as SSMS's: databases (system ones in their own folder), tables, views, synonyms, programmability, security, columns, keys, indexes, triggers, parameters, logins and server roles.
- **Scripts:** Select Top 1000 Rows, Script as CREATE (tables, views, procedures, functions, triggers) and Script as EXECUTE.
- **The blocking manager**, under Management: blocking chains under their lead blockers, and kill, audited as `mssql.kill`.

**Not yet:** a named instance's port from the SQL Server Browser (set the service's port), Orphaned Users, the insurance survey, and the internal scan's SQL Server detection. A self-signed certificate SQL Server makes anew at each restart has to be trusted again by someone who manages the host, since core refuses a changed certificate (1.x asked again).

The plugin's ID is `mssql`, and it is built in: it is signed with NKeeper's official key and installed with NKeeper.

## Layout

```text
backend/        the plugin: SqlServerPlugin.cs, the engine and dialect (SqlServerEngine.cs), the certificate probe, the query runner, explorer, scripter and blocking, ported from 1.x; nkeeper-plugin.json (the manifest), the project
frontend/       its tab: db-core's database tab, told it's SQL Server
tests/          the engine and db-core's session against a real SQL Server in Docker, and the GO splitter
.github/        CI and the release workflow
```

It builds against NKeeper's published SDK and db-core's package, never core's source:

| Package | Registry | Used for |
| --- | --- | --- |
| `NKeeper.Abstractions` | GitHub Packages (NuGet) | the backend SDK |
| `NKeeper.Plugins.Databases` | GitHub Packages (NuGet) | db-core's assembly: the engine contract and the session |
| `NKeeper.Plugin.Build` | GitHub Packages (NuGet) | builds, packs and signs the plugin |
| `Microsoft.Data.SqlClient` | nuget.org | the driver, which the package carries |
| `@opencraft-solutions/nkeeper-sdk` | GitHub Packages (npm) | the frontend SDK, imported as `@nkeeper/sdk` |

The plugin is built for `linux-x64`, where NKeeper runs: SqlClient's portable assembly only says the platform isn't supported, and the package carries its real one.

## Building

You need the .NET 10 SDK (see `global.json`), Node.js 24 (see `.nvmrc`), and Docker for the tests.

GitHub Packages needs a token even for public packages. Make a GitHub personal access token that can read packages (`read:packages`) and give it to the build in the environment. It is never written to the repository:

```bash
export NUGET_USERNAME=<your GitHub user name>
export NUGET_AUTH_TOKEN=<token>
export NODE_AUTH_TOKEN=<token>
```

Then, from the repository root:

```bash
(cd frontend && npm ci && npm run lint && npm run typecheck)
dotnet restore NKeeper.Plugins.SqlServer.slnx
dotnet build NKeeper.Plugins.SqlServer.slnx --configuration Release
```

Building the plugin builds its frontend, packs `mssql-<version>.nkp` into `artifacts/plugins/` and signs it with a development key, which the build creates under `.dev/keys/` on first use. Both are ignored by git. To load it in a development NKeeper, put db-core's package beside it.

Pinned versions: NuGet packages are locked in `packages.lock.json` (CI restores with `--locked-mode`), and npm packages in `frontend/package-lock.json`. Run `dotnet restore --force-evaluate` after changing a version.

## Testing

```bash
dotnet test NKeeper.Plugins.SqlServer.slnx --configuration Release
```

The tests start SQL Server 2022 in Docker (Testcontainers), with its self-signed certificate, and are skipped when Docker isn't there. A stand-in for core opens the TCP connection, answers for the trust store and opens the login, as core does. They cover, ported from 1.x:
- the certificate: asked about, then pinned, the pinned copy removed with the login, and the connection encrypted;
- a wrong password, without echoing it, and a Windows login refused;
- batches in order with results, messages and errors at their lines; a window's state, its database and a cancelled query;
- the explorer and scripts, and a name that's quoted, never pasted in;
- the blocking manager's chains, and kill with its audit.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request: it restores from GitHub Packages with the workflow's token, lints and type-checks the frontend, checks formatting, builds, runs the tests and packs the plugin signed with a development key, which it keeps as a build artifact.

## Releasing

1. Raise `version` in `backend/nkeeper-plugin.json` (and keep `frontend/package.json` in step), and merge it to `main`.
2. Tag the merge commit `v<version>`, for example `v1.1.0`, and push the tag.
3. `.github/workflows/release.yml` runs. It checks that the tag matches the manifest's version and is on `main`, builds and tests, and then, in the protected `release` environment, which needs an owner's approval, packs `mssql-<version>.nkp` signed with the official key and publishes a GitHub release with the package and its SHA-256.

The official key's private half, `NKEEPER_RELEASE_KEY` (a PEM), is a secret of the `release` environment. It is available only to the signing job, goes to memory-backed storage for the packing step alone, and is removed when that step ends. It is never in the repository or in a log.

NKeeper's installer picks the release up when its `deploy/plugins.lock` is updated with the new URL and SHA-256.

## License

MIT, as NKeeper. See [LICENSE](LICENSE).
