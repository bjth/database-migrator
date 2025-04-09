# Migrator

[![Codacy Badge](https://app.codacy.com/project/badge/Grade/2a1f8ce4fec44dbf8b3791b6cde7947c)](https://app.codacy.com/gh/bjth/database-migrator/dashboard?utm_source=gh&utm_medium=referral&utm_content=&utm_campaign=Badge_grade)
[![Docker Publish Workflow](https://github.com/bjth/database-migrator/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/bjth/database-migrator/actions/workflows/docker-publish.yml)
[![License: MIT](https://img.shields.io/github/license/bjth/database-migrator)](LICENSE)
[![Latest Tag](https://img.shields.io/github/v/tag/bjth/database-migrator?sort=semver&label=latest)](https://github.com/bjth/database-migrator/tags)

A generic database migrator tool for .NET 9, supporting MSSQL, PostgreSQL, and SQLite.

## Goals

*   Provide a simple command-line tool to apply database migrations.
*   Support migrations written as C# classes (using FluentMigrator) or plain SQL scripts.
*   Allow dynamic discovery of migrations from external assemblies (DLLs) and SQL files placed in a specific folder.
*   **Execute all discovered migrations (C# and SQL) strictly in order based on a `yyyyMMddHHmmss` timestamp** derived from the `[Migration]` attribute in C# or the filename prefix for SQL scripts.
*   Support common database providers: SQL Server, PostgreSQL, and SQLite.
*   Include integration tests using Testcontainers (for SQL Server/PostgreSQL) and file-based testing (for SQLite).

## Structure

*   `Migrator.Core`: Class library containing the core migration logic (discovery, ordering, execution).
*   `Migrator.Runner`: Console application providing the command-line interface. Also includes the `Dockerfile` for the `bthdev/db-migration-runner` image.
*   `Migrator.Tests`: xUnit test project with integration tests.
*   `ExampleMigrations`: Sample class library showing how to create migration DLLs (targets .NET 9 and .NET Standard 2.0).

## Getting Started

1.  **Build the Solution:**
    ```bash
    dotnet build Migrator.sln
    ```
2.  **Prepare Migrations:**
    *   Create migration classes inheriting from `FluentMigrator.Migration` in a separate class library project (like `ExampleMigrations`).
    *   Decorate your C# migration classes with `[Migration(YYYYMMDDHHMM)]`, where the number is a **12-digit timestamp**. This timestamp determines the execution order relative to other migrations.
    *   Alternatively (or additionally), create `.sql` script files.
    *   Build your migrations project.
    *   Create a dedicated folder (e.g., `./migrations`).
    *   Copy the compiled migration `.dll` file(s) (containing your C# migrations) into the migrations folder. The DLL filenames themselves do not influence the execution order; only the `[Migration]` attribute timestamp matters.
    *   Copy any `.sql` script files into the migrations folder, naming them with a `YYYYMMDDHHMM` (12-digit) timestamp prefix (e.g., `202504091001_AddIndexes.sql`). This prefix determines their execution order relative to other migrations (both SQL and C#).
3.  **Run the Migrator:**
    Use the `Migrator.Runner` executable:
    ```bash
    # Example using relative paths after building
    ./src/Migrator.Runner/bin/Debug/net9.0/Migrator.Runner.exe --type SqlServer --connection "Your_Connection_String" --path "./migrations"

    # Or for PostgreSQL
    ./src/Migrator.Runner/bin/Debug/net9.0/Migrator.Runner.exe -t PostgreSql -c "Your_Pg_Connection_String" -p "./migrations"

    # Or for SQLite
    ./src/Migrator.Runner/bin/Debug/net9.0/Migrator.Runner.exe -t SQLite -c "Data Source=./myDatabase.db" -p "./migrations"
    ```
    Use the `-v` or `--verbose` flag for more detailed logging.

4.  **Running Tests:**
    Requires Docker to be installed and running for SQL Server and PostgreSQL tests.
    ```bash
    dotnet test
    ```

## Running in Docker (`bthdev/db-migration-runner`)

The `bthdev/db-migration-runner` image packages the migrator tool for easy execution in containerized environments. See the [Docker Hub Repository Overview](https://hub.docker.com/r/bthdev/db-migration-runner) for detailed usage instructions, including environment variables and examples for Docker and Kubernetes.

**Quick Example (Linux/macOS):**
```bash
docker run --rm \
  -v /path/to/my-app-migrations:/app/migrations \
  -v /path/to/my-logs:/app/logs \
  -e DATABASE_TYPE="SqlServer" \
  -e CONNECTION_STRING="Your_Connection_String" \
  bthdev/db-migration-runner:latest
```
*   Mount your migrations into `/app/migrations`.
*   Mount a logs directory to `/app/logs` to capture error logs.
*   Set `DATABASE_TYPE` and `CONNECTION_STRING` environment variables.

## Running in Kubernetes (AKS Example)

See the [Docker Hub Repository Overview](https://hub.docker.com/r/bthdev/db-migration-runner) for a detailed example of running the migrator as a Kubernetes Job.

## Key Dependencies & Acknowledgements

This project relies on several excellent open-source libraries:

*   **[FluentMigrator](https://fluentmigrator.github.io/)** (Apache-2.0 License): Used for defining and executing C# migrations and executing SQL scripts.
*   **[Testcontainers for .NET](https://testcontainers.com/guides/getting-started-with-testcontainers-for-dotnet/)** (MIT License): Used for running integration tests against real database instances in Docker containers (SQL Server, PostgreSQL).
*   **[CommandLineParser Library](https://github.com/commandlineparser/commandline)** (MIT License): Used for parsing command-line arguments in the runner application.
*   **[Serilog](https://serilog.net/)** (Apache-2.0 License): Used for structured logging.
*   **[xUnit.net](https://xunit.net/)** (Apache-2.0 License): Used as the test framework.
*   **[Shouldly](https://shouldly.readthedocs.io/)** (BSD-3-Clause License): Used for assertions in tests.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details. 