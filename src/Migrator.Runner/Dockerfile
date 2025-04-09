# Use the .NET SDK image for building
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env
WORKDIR /app

# Copy everything and restore dependencies
COPY . .
WORKDIR /app/src/Migrator.Runner
RUN dotnet restore

# Build and publish the runner application
RUN dotnet publish -c Release -o /app/out

# Use the .NET runtime image for the final stage
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app
COPY --from=build-env /app/out .

# Set the entrypoint to the Migrator.Runner executable
ENTRYPOINT ["dotnet", "Migrator.Runner.dll"]

# Default command arguments (can be overridden)
# Example: CMD ["--type", "SqlServer", "--connection", "...", "--path", "/app/migrations"]
CMD [] 