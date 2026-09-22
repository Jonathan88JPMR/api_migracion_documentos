using api_migracion_documentos.Application.UseCaseImpl;
using api_migracion_documentos.Domain.Repository;
using api_migracion_documentos.Domain.UseCase;
using api_migracion_documentos.Infraestructure.Persistence;
using api_migracion_documentos.Infraestructure.RepositoryImpl;
using api_migracion_documentos.Infraestructure.Services;
using api_migracion_documentos.Infraestructure.Shared.Exceptions;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy => policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader());
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "API Migracion Documentos",
        Version = "v1",
        Description = "API del dashboard de migración de adjuntos portal -> Spring Gestión Documental"
    });
});

builder.Services.AddSingleton<ConexionFactory>();
builder.Services.AddSingleton<ServicioFiles>();
builder.Services.AddSingleton<RunManager>();
builder.Services.AddScoped<IMigracionMsRepository, MigracionMsRepositoryImpl>();
builder.Services.AddScoped<IMigracionSpringRepository, MigracionSpringRepositoryImpl>();
builder.Services.AddScoped<IMigracionUseCase, MigracionUseCase>();

var app = builder.Build();

app.UseCors("AllowAll");
app.UseMiddleware<ExceptionMiddleware>();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "API Migracion Documentos v1");
    c.DocumentTitle = "API Migracion Documentos Docs";
});

app.MapControllers();

// Sirve el frontend Angular (dist copiado a wwwroot) en el mismo sitio IIS.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
