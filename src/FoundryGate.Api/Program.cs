using FoundryGate.Data;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("FoundryGate")
    ?? throw new InvalidOperationException(
        "Missing required configuration 'ConnectionStrings:FoundryGate'.");

builder.Services.AddFoundryGateData(connectionString);

var app = builder.Build();

app.Run();
