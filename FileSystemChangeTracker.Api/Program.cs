using FileSystemChangeTracker.Api.Options;
using FileSystemChangeTracker.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.Configure<AnalysisOptions>(builder.Configuration.GetSection(AnalysisOptions.SectionName));

// Stavové komponenty (registr, fronta) musí být singletony; ostatní jsou bezstavové a thread-safe.
builder.Services.AddSingleton<IPathPolicy, PathPolicy>();
builder.Services.AddSingleton<IFileHasher, Sha256FileHasher>();
builder.Services.AddSingleton<IDirectoryScanner, DirectoryScanner>();
builder.Services.AddSingleton<ISnapshotComparer, SnapshotComparer>();
builder.Services.AddSingleton<ISnapshotStore, JsonSnapshotStore>();
builder.Services.AddSingleton<IAnalysisService, AnalysisService>();
builder.Services.AddSingleton<IAnalysisRegistry, AnalysisRegistry>();
builder.Services.AddSingleton<IAnalysisBacklog, AnalysisBacklog>();
builder.Services.AddHostedService<AnalysisWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Generuje OpenAPI dokument na /openapi/v1.json a nad ním zobrazí Swagger UI na /swagger.
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "FileSystem Change Tracker API"));
}

app.UseHttpsRedirection();

// Mini UI (wwwroot/index.html): textbox pro cestu, spuštění analýzy a živý průběh přes SSE.
// Čistý statický klient REST API — žádná serverová logika navíc.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Bez Cache-Control si prohlížeč odvodí platnost heuristicky z Last-Modified a při běžné
    // navigaci může několik minut servírovat starou verzi stránky/skriptu (bez revalidace).
    // no-cache = vždy revalidovat přes ETag; nezměněný soubor stále vrátí 304.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapControllers();
app.Run();

/// <summary>Zviditelnění vstupního bodu pro integrační testy (WebApplicationFactory&lt;Program&gt;).</summary>
public partial class Program;
