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
    *   Decorate your C# migration classes with `[Migration(YYYYMMDDHHMM)]`, where the number is a **12-digit timestamp**. This timestamp determines the execution order relative to other migrations.
    *   Alternatively, create `.sql` script files.
    *   Build your migrations project.
    *   Create a dedicated folder (e.g., `./migrations`).
    *   Copy the compiled migration `.dll` file(s) (containing your C# migrations) into the migrations folder. The DLL filenames themselves do not influence the execution order.
    *   Copy any `.sql` script files into the migrations folder, naming them with a `YYYYMMDDHHMM` (12-digit) timestamp prefix (e.g., `202504091001_AddIndexes.sql`). This prefix determines their execution order relative to other migrations.
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

## Running in Docker

1.  **Build the Image:** (Optional, if not using pre-built from Docker Hub)
    ```bash
    docker build -t bthdev/db-migration-runner -f src/Migrator.Runner/Dockerfile .
    ```
2.  **Run the Container:** Mount your local migrations folder into the container and provide arguments. Use the image `bthdev/db-migration-runner:latest` (or a specific version tag).

    **Windows (PowerShell/CMD):**
    ```bash
    docker run --rm `
      -v C:\my-app-migrations:/app/migrations `
      bthdev/db-migration-runner:latest `
      --type SqlServer `
      --connection "Your_Connection_String_Accessible_From_Container" `
      --path /app/migrations
    ```

    **Linux/macOS:**
    ```bash
    docker run --rm \
      -v /path/to/my-app-migrations:/app/migrations \
      bthdev/db-migration-runner:latest \
      --type SqlServer \
      --connection "Your_Connection_String_Accessible_From_Container" \
      --path /app/migrations
    ```
    *   Replace the host path (`C:\...` or `/path/to/...`) with the absolute path to your local migrations folder.
    *   Ensure the `--path` argument points to `/app/migrations` (the mount path inside the container).
    *   Make sure the database is accessible from the container (use Docker networking like `host.docker.internal` if needed).

## Running in Kubernetes (AKS Example)

This example demonstrates running the migrator as a Kubernetes Job, suitable for CI/CD pipelines. It uses an init container to fetch migrations from a Git repository onto a shared volume.

**Assumptions:**
*   Kubernetes cluster (like AKS) is available.
*   The `bthdev/db-migration-runner` Docker image is available on Docker Hub (or another accessible registry).
*   Database connection string is stored in a K8s secret named `db-credentials` with key `connectionString`.
*   Migrations (DLLs/SQL files) are in a Git repository.

**Example `migration-job.yaml`:**

```yaml
apiVersion: batch/v1
kind: Job
metadata:
  name: database-migration-job
spec:
  template:
    spec:
      volumes:
      - name: migration-files # Shared volume for migrations
        emptyDir: {} 
      - name: migration-logs # Optional: Volume for logs
        emptyDir: {}

      initContainers: # Fetches migrations before the main container runs
      - name: git-sync-migrations
        image: alpine/git:latest # Small image with git
        command: ["/bin/sh", "-c"]
        args:
        - |
          echo "Cloning migrations repository..."
          # --- CUSTOMIZE --- 
          # Replace with your Git repo URL and branch/tag.
          # Use SSH keys mounted as secrets for private repos if needed.
          GIT_REPO="https://github.com/your-repo/your-migrations.git"
          GIT_BRANCH="main"
          # Path within the repo where built migrations are located
          REPO_MIGRATIONS_PATH="src/ExampleMigrations/bin/Release/net9.0"
          # --- END CUSTOMIZE ---
          git clone --branch $GIT_BRANCH --depth 1 $GIT_REPO /migrations-repo
          echo "Copying migrations from $REPO_MIGRATIONS_PATH..."
          cp -r /migrations-repo/$REPO_MIGRATIONS_PATH/* /migration-volume/
          echo "Migrations copied to shared volume."
        volumeMounts:
        - name: migration-files # Mount the shared volume
          mountPath: /migration-volume 

      containers: # Main migration container
      - name: migrator-runner
        # --- CUSTOMIZE --- 
        image: bthdev/db-migration-runner:latest # Use the image from Docker Hub (or specific version)
        # --- END CUSTOMIZE ---
        command: ["dotnet", "Migrator.Runner.dll"]
        args:
          # --- CUSTOMIZE --- 
          - "--type"
          - "SqlServer" # Or PostgreSql, SQLite
          - "--connection"
          - "$(DB_CONNECTION_STRING)" # Reference the secret
          - "--path"
          - "/app/migrations" # Must match volumeMounts.mountPath below
          # Optional: Add --verbose
          # --- END CUSTOMIZE ---
        env:
          - name: DB_CONNECTION_STRING
            valueFrom:
              secretKeyRef:
                name: db-credentials # Name of the Kubernetes secret
                key: connectionString # Key within the secret
        volumeMounts:
        - name: migration-files # Mount the shared volume containing migrations
          mountPath: /app/migrations 
        - name: migration-logs # Mount the log volume
          mountPath: /app/logs # Matches path used by WriteErrorLogAsync

      restartPolicy: Never # Or OnFailure
  backoffLimit: 1
```

**Steps:**

1.  **Create Secret:** Store the DB connection string:
    ```bash
    kubectl create secret generic db-credentials \
      --from-literal=connectionString='Your_Actual_Database_Connection_String'
    ```
2.  **Customize YAML:** Update the `GIT_REPO`, `GIT_BRANCH`, `REPO_MIGRATIONS_PATH`, and `--type` in the `migration-job.yaml` file. Ensure the `image:` path is correct.
3.  **Apply Job:**
    ```bash
    kubectl apply -f migration-job.yaml
    ```
4.  **Monitor:**
    ```bash
    kubectl get jobs
    kubectl describe job database-migration-job
    kubectl logs job/database-migration-job -c migrator-runner # Main logs
    kubectl logs job/database-migration-job -c git-sync-migrations # Init logs
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