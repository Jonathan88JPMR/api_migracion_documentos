using System.Text.Json;
using System.Text.Json.Nodes;

namespace api_migracion_documentos.Domain.UseCase
{
    /// <summary>Cuerpo JSON + código HTTP (para 403/404 como los devuelve Flask).</summary>
    public record ApiResult(JsonObject Body, int Status = 200);

    public interface IMigracionUseCase
    {
        Task<ApiResult> EntornosAsync();
        Task<ApiResult> CambiarEntornoAsync(string entorno, string ip);
        Task<ApiResult> ConfigGetAsync();
        Task<ApiResult> ConfigUpdateAsync(JsonElement updates);
        Task<ApiResult> StatsAsync();
        Task<ApiResult> ArchivosAsync(JsonElement filtros);
        Task<(byte[]? datos, string nombre, string? error)> ArchivosExcelAsync(JsonElement filtros);
        Task<(byte[] datos, string nombre, string mime)?> PreviewApAsync(int id);
        Task<(byte[] datos, string nombre, string mime)?> PreviewMsAsync(string id);
        Task<ApiResult> LogAsync(int tail);
        Task<ApiResult> RunAsync();
        Task<ApiResult> RunAllAsync();
        Task<ApiResult> RunOrdenAsync(string numero, string? tipo);
        Task<ApiResult> RunRangoAsync(JsonElement body);
        ApiResult RunStatus(string runId);
    }
}
