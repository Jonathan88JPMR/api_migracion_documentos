using System.Text.Json;
using api_migracion_documentos.Domain.Repository;
using Microsoft.Data.SqlClient;

namespace api_migracion_documentos.Infraestructure.RepositoryImpl
{
    public class MigracionMsRepositoryImpl : BaseRepository, IMigracionMsRepository
    {
        public Task<JsonElement> EntornosAsync(SqlConnection conn) => EjecutarSpJsonAsync(conn, "dbo.sp_mig_entornos", null);
        public Task<JsonElement> EntornoActivoAsync(SqlConnection conn, string json) => EjecutarSpJsonAsync(conn, "dbo.sp_mig_entorno_activo", json);
        public Task<JsonElement> ConfigAsync(SqlConnection conn, string json) => EjecutarSpJsonAsync(conn, "dbo.sp_mig_config", json);
        public Task<JsonElement> StatsAsync(SqlConnection conn) => EjecutarSpJsonAsync(conn, "dbo.sp_mig_stats", null);
        public Task<JsonElement> CandidatosApAsync(SqlConnection conn, string json) => EjecutarSpJsonAsync(conn, "dbo.sp_mig_candidatos_ap", json);
        public Task<JsonElement> ArchivosApAsync(SqlConnection conn, string json) => EjecutarSpJsonAsync(conn, "dbo.sp_mig_archivos_ap", json);
        public Task<JsonElement> ArchivosMsAsync(SqlConnection conn, string json) => EjecutarSpJsonAsync(conn, "dbo.sp_mig_archivos_ms", json);
        public Task<List<Dictionary<string, object?>>> PreviewApAsync(SqlConnection conn, string json) => EjecutarSpRowsetAsync(conn, "dbo.sp_mig_preview_ap", json);
        public Task<List<Dictionary<string, object?>>> PreviewMsAsync(SqlConnection conn, string json) => EjecutarSpRowsetAsync(conn, "dbo.sp_mig_preview_ms", json);
        public Task<JsonElement> PendientesAsync(SqlConnection conn) => EjecutarSpJsonAsync(conn, "dbo.sp_mig_pendientes", null);
        public Task<JsonElement> PendientesOrdenAsync(SqlConnection conn, string json) => EjecutarSpJsonAsync(conn, "dbo.sp_mig_pendientes_orden", json);
        public Task<JsonElement> ActualizarLotesAsync(SqlConnection conn, string json) => EjecutarSpJsonAsync(conn, "dbo.sp_mig_actualizar_lotes", json);
    }
}
