using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace api_migracion_documentos.Domain.Repository
{
    /// <summary>Acceso a MS_HASS_PROVEEDOR (portal) vía SPs sp_mig_*.</summary>
    public interface IMigracionMsRepository
    {
        Task<JsonElement> EntornosAsync(SqlConnection conn);
        Task<JsonElement> EntornoActivoAsync(SqlConnection conn, string json);
        Task<JsonElement> ConfigAsync(SqlConnection conn, string json);
        Task<JsonElement> StatsAsync(SqlConnection conn);
        Task<JsonElement> CandidatosApAsync(SqlConnection conn, string json);
        Task<JsonElement> ArchivosApAsync(SqlConnection conn, string json);
        Task<JsonElement> ArchivosMsAsync(SqlConnection conn, string json);
        Task<List<Dictionary<string, object?>>> PreviewApAsync(SqlConnection conn, string json);
        Task<List<Dictionary<string, object?>>> PreviewMsAsync(SqlConnection conn, string json);
        Task<JsonElement> PendientesAsync(SqlConnection conn);
        Task<JsonElement> PendientesOrdenAsync(SqlConnection conn, string json);
        Task<JsonElement> ActualizarLotesAsync(SqlConnection conn, string json);
    }

    /// <summary>Acceso a HASS_SPRING (AP_Documentos / GD_Archivo) vía SPs sp_mig_*.</summary>
    public interface IMigracionSpringRepository
    {
        Task<JsonElement> ReferenciasAsync(SqlConnection conn, string json);
        Task<JsonElement> ClavesPorRefAsync(SqlConnection conn, string json);
        Task<JsonElement> ObligacionesReferenciaAsync(SqlConnection conn, string json);
        Task<JsonElement> GdPruebaCountAsync(SqlConnection conn);
    }
}
