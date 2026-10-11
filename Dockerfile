FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source
COPY . .
RUN dotnet restore Octo.IntegrationLab.slnx --locked-mode
RUN dotnet publish src/Lab/Lab.csproj -c Release --no-restore -o /out/lab \
    && dotnet publish src/Partner/Partner.csproj -c Release --no-restore -o /out/partner \
    && dotnet build tests/Lab.Tests/Lab.Tests.csproj -c Release --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
USER $APP_UID

FROM runtime AS lab
COPY --from=build /out/lab .
ENTRYPOINT ["dotnet", "Lab.dll"]

FROM runtime AS partner
COPY --from=build /out/partner .
ENTRYPOINT ["dotnet", "Partner.dll"]

FROM build AS tests
ENTRYPOINT ["dotnet", "test", "tests/Lab.Tests/Lab.Tests.csproj", "-c", "Release", "--no-build", "--logger", "console;verbosity=normal"]
