using System.Data;
using System.Text;
using System.Text.Json;
using api_migracion_documentos.Domain.Repository;
using Microsoft.Data.SqlClient;

namespace api_migracion_documentos.Infraestructure.RepositoryImpl
{
    /// <summary>
    /// Acceso a Spring (AP_Documentos / GD_Archivo).
    /// Convención: llama a los SPs sp_mig_* (Scripts/02_sp_spring.sql).
    /// Fallback: si el SP no está desplegado aún en la base (el usuario del
    /// servicio solo tiene SELECT), ejecuta la consulta equivalente en línea.
    /// Cuando el DBA despliegue los SPs se usan automáticamente.
    /// </summary>
    public class MigracionSpringRepositoryImpl : BaseRepository, IMigracionSpringRepository
    {
        private static bool SpAusente(Exception ex) =>
            ex is SqlException sqlEx && (sqlEx.Number == 2812 || sqlEx.Message.Contains("Could not find stored procedure"));

        private static JsonElement Parse(string json) =>
            JsonDocument.Parse(json).RootElement.Clone();

        /// <summary>Inserta claves (p,t,n) en tabla temporal para JOIN.</summary>
        private static async Task CrearTablaClaves(SqlConnection conn, JsonElement claves)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "IF OBJECT_ID('tempdb..#claves') IS NOT NULL DROP TABLE #claves; " +
                              "CREATE TABLE #claves (p VARCHAR(20), t VARCHAR(5), n VARCHAR(30))";
            await cmd.ExecuteNonQueryAsync();
            var lista = claves.EnumerateArray()
                .Select(c => (p: c.GetProperty("p").GetString() ?? "", t: c.GetProperty("t").GetString() ?? "", n: c.GetProperty("n").GetString() ?? ""))
                .ToList();
            const int chunk = 500;
            for (var i = 0; i < lista.Count; i += chunk)
            {
                var grupo = lista.Skip(i).Take(chunk).ToList();
                var sb = new StringBuilder("INSERT INTO #claves (p,t,n) VALUES ");
                var vals = new List<string>();
                for (var j = 0; j < grupo.Count; j++)
                    vals.Add($"(@p{i + j},@t{i + j},@n{i + j})");
                sb.Append(string.Join(",", vals));
                await using var ins = conn.CreateCommand();
                ins.CommandText = sb.ToString();
                for (var j = 0; j < grupo.Count; j++)
                {
                    ins.Parameters.AddWithValue($"@p{i + j}", grupo[j].p);
                    ins.Parameters.AddWithValue($"@t{i + j}", grupo[j].t);
                    ins.Parameters.AddWithValue($"@n{i + j}", grupo[j].n);
                }
                await ins.ExecuteNonQueryAsync();
            }
        }

        private static bool TieneClaves(JsonElement claves) =>
            claves.ValueKind == JsonValueKind.Array && claves.GetArrayLength() > 0;

        public async Task<JsonElement> ReferenciasAsync(SqlConnection conn, string json)
        {
            try { return await EjecutarSpJsonAsync(conn, "dbo.sp_mig_referencias", json); }
            catch (Exception ex) when (SpAusente(ex)) { }

            var doc = JsonDocument.Parse(json).RootElement;
            var claves = doc.GetProperty("claves");
            if (!TieneClaves(claves)) return Parse("[]");
            await conn.OpenAsync();
            try
            {
                await CrearTablaClaves(conn, claves);
                var rows = new List<object>();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT CAST(d.Proveedor AS VARCHAR(20)) p,
                           RTRIM(d.ObligacionTipoDocumento) t,
                           RTRIM(d.ObligacionNumeroDocumento) n,
                           RTRIM(d.ReferenciaTipoDocumento) refTipo,
                           RTRIM(d.ReferenciaNumeroDocumento) refNum
                    FROM dbo.AP_Documentos d
                    JOIN #claves c ON CAST(d.Proveedor AS VARCHAR(20)) = c.p
                                  AND RTRIM(d.ObligacionTipoDocumento) = c.t
                                  AND RTRIM(d.ObligacionNumeroDocumento) = c.n
                    WHERE d.ReferenciaTipoDocumento IS NOT NULL
                      AND d.ReferenciaNumeroDocumento IS NOT NULL";
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    rows.Add(new { p = rd.GetString(0), t = rd.GetString(1), n = rd.GetString(2), refTipo = rd.GetString(3), refNum = rd.GetString(4) });
                return Parse(JsonSerializer.Serialize(rows));
            }
            finally { await conn.CloseAsync(); }
        }

        public async Task<JsonElement> ClavesPorRefAsync(SqlConnection conn, string json)
        {
            try { return await EjecutarSpJsonAsync(conn, "dbo.sp_mig_claves_por_ref", json); }
            catch (Exception ex) when (SpAusente(ex)) { }

            var doc = JsonDocument.Parse(json).RootElement;
            var refTipo = doc.GetProperty("refTipo").GetString() ?? "";
            var claves = doc.GetProperty("claves");
            if (!TieneClaves(claves)) return Parse("[]");
            await conn.OpenAsync();
            try
            {
                await CrearTablaClaves(conn, claves);
                var rows = new List<object>();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT DISTINCT CAST(d.Proveedor AS VARCHAR(20)) p,
                           RTRIM(d.ObligacionTipoDocumento) t,
                           RTRIM(d.ObligacionNumeroDocumento) n
                    FROM dbo.AP_Documentos d
                    JOIN #claves c ON CAST(d.Proveedor AS VARCHAR(20)) = c.p
                                  AND RTRIM(d.ObligacionTipoDocumento) = c.t
                                  AND RTRIM(d.ObligacionNumeroDocumento) = c.n
                    WHERE RTRIM(d.ReferenciaTipoDocumento) = @refTipo";
                cmd.Parameters.AddWithValue("@refTipo", refTipo);
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    rows.Add(new { p = rd.GetString(0), t = rd.GetString(1), n = rd.GetString(2) });
                return Parse(JsonSerializer.Serialize(rows));
            }
            finally { await conn.CloseAsync(); }
        }

        public async Task<JsonElement> ObligacionesReferenciaAsync(SqlConnection conn, string json)
        {
            try { return await EjecutarSpJsonAsync(conn, "dbo.sp_mig_obligaciones_referencia", json); }
            catch (Exception ex) when (SpAusente(ex)) { }

            var doc = JsonDocument.Parse(json).RootElement;
            var numero = doc.GetProperty("numero").GetString() ?? "";
            var tipo = doc.TryGetProperty("tipo", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null;

            var rows = new List<object>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT CAST(d.Proveedor AS VARCHAR(20)) p,
                       RTRIM(d.ObligacionTipoDocumento) t,
                       RTRIM(d.ObligacionNumeroDocumento) n
                FROM dbo.AP_Documentos d
                WHERE RTRIM(d.ReferenciaNumeroDocumento) = @numero
                  AND (@tipo IS NULL OR RTRIM(d.ReferenciaTipoDocumento) = @tipo)";
            cmd.Parameters.AddWithValue("@numero", numero);
            cmd.Parameters.AddWithValue("@tipo", (object?)tipo ?? DBNull.Value);
            await conn.OpenAsync();
            try
            {
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    rows.Add(new { p = rd.GetString(0), t = rd.GetString(1), n = rd.GetString(2) });
                return Parse(JsonSerializer.Serialize(rows));
            }
            finally { await conn.CloseAsync(); }
        }

        public async Task<JsonElement> GdPruebaCountAsync(SqlConnection conn)
        {
            try { return await EjecutarSpJsonAsync(conn, "dbo.sp_mig_gd_prueba_count", null); }
            catch (Exception ex) when (SpAusente(ex)) { }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT COUNT(*) FROM dbo.GD_Archivo
                                WHERE RutaArchivo LIKE '%SpringGestionDoc\PRUEBAS%'";
            await conn.OpenAsync();
            try
            {
                var total = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                return Parse(JsonSerializer.Serialize(new { total }));
            }
            finally { await conn.CloseAsync(); }
        }
    }
}
