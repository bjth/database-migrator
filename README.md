# Migrator

A generic database migrator tool for .NET 9, supporting MSSQL, PostgreSQL, and SQLite.

## Goals

*   Provide a simple command-line tool to apply database migrations.
*   Support migrations written as C# classes (using FluentMigrator) or plain SQL scripts.
*   Allow dynamic discovery of migrations from external assemblies (DLLs) and SQL files placed in a specific folder.
*   Order migrations based on a `yyyyMMddHHmmss` timestamp prefix in the filename (for both DLLs and SQL scripts).
*   Support common database providers: SQL Server, PostgreSQL, and SQLite.
*   Include integration tests using Testcontainers (for SQL Server/PostgreSQL) and file-based testing (for SQLite).

## Structure

*   `Migrator.Core`: Class library containing the core migration logic (discovery, ordering, execution).
*   `Migrator.Runner`: Console application providing the command-line interface.
*   `Migrator.Tests`: xUnit test project with integration tests.
*   `ExampleMigrations`: Sample class library showing how to create migration DLLs (targets .NET 9 and .NET Standard 2.0).

## Getting Started

1.  **Build the Solution:**
    ```bash
    dotnet build Migrator.sln
    ```
2.  **Prepare Migrations:**
    *   Create migration classes inheriting from `FluentMigrator.Migration` in a separate class library project (like `ExampleMigrations`).
    *   Decorate your migration classes with `[Migration(YYYYMMDDHHMMSS)]`, where the number is a 14-digit timestamp.
    *   Alternatively, create `.sql` script files.
    *   Build your migrations project.
    *   Create a dedicated folder (e.g., `./migrations`).
    *   Copy the compiled migration `.dll` file into the migrations folder, renaming it to start with the exact same `YYYYMMDDHHMMSS` timestamp used in the `[Migration]` attribute (e.g., `20250409100000_MyMigrationLib.dll`).
    *   Copy any `.sql` script files into the migrations folder, naming them with a `YYYYMMDDHHMMSS` timestamp prefix (e.g., `20250409100100_AddIndexes.sql`).
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