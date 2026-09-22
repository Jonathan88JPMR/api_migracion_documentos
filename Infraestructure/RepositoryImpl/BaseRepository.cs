using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace api_migracion_documentos.Infraestructure.RepositoryImpl
{
    /// <summary>
    /// Ejecuta stored procedures con parámetro @json y salida FOR JSON,
    /// igual que BaseRepository de api_alquiler_maquinaria pero sobre
    /// SqlConnection directa (dos bases con conexión dinámica por entorno).
    /// </summary>
    public abstract class BaseRepository
    {
        /// <summary>
        /// Ejecuta un SP que devuelve {status,message,data} FOR JSON.
        /// Devuelve el JsonElement de "data" (o lanza si status=error).
        /// </summary>
        protected static async Task<JsonElement> EjecutarSpJsonAsync(SqlConnection conn, string spName, string? json)
        {
            var sb = new StringBuilder();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = spName;
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandTimeout = 120;
            if (json != null)
                cmd.Parameters.Add(new SqlParameter("@json", SqlDbType.NVarChar, -1) { Value = json });

            try
            {
                await conn.OpenAsync();
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    sb.Append(rd.IsDBNull(0) ? "" : rd.GetString(0));
            }
            finally
            {
                if (conn.State == ConnectionState.Open) await conn.CloseAsync();
            }

            var txt = sb.ToString();
            if (string.IsNullOrWhiteSpace(txt)) return default;
            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var st)
                && st.GetString() == "error")
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "Error en SP";
                throw new InvalidOperationException(msg);
            }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
            {
                // sp_mig_respuesta devuelve data como string JSON escapado
                // (NVARCHAR dentro de FOR JSON): se parsea a elemento real.
                if (data.ValueKind == JsonValueKind.String)
                {
                    var inner = data.GetString();
                    if (string.IsNullOrWhiteSpace(inner)) return default;
                    return JsonDocument.Parse(inner).RootElement.Clone();
                }
                return data.Clone();
            }
            return root.Clone();
        }

        /// <summary>Ejecuta un SP que devuelve un rowset (previews binarios).</summary>
        protected static async Task<List<Dictionary<string, object?>>> EjecutarSpRowsetAsync(
            SqlConnection conn, string spName, string json)
        {
            var rows = new List<Dictionary<string, object?>>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = spName;
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.Parameters.Add(new SqlParameter("@json", SqlDbType.NVarChar, -1) { Value = json });

            try
            {
                await conn.OpenAsync();
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var row = new Dictionary<string, object?>();
                    for (var i = 0; i < rd.FieldCount; i++)
                        row[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    rows.Add(row);
                }
            }
            finally
            {
                if (conn.State == ConnectionState.Open) await conn.CloseAsync();
            }
            return rows;
        }
    }
}
