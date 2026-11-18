using QuantDesk.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();

// Bootstrap ONLY. Authentication, BFF, persistence, SignalR and real modules are QD implementation tasks.
app.MapGet("/health", () => Results.Ok(new { status = "healthy", application = "QuantDesk.Api" }));
app.MapGet("/api/meta/modules", () => Results.Ok(ModuleCatalog.All));

app.Run();

public partial class Program { }