using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using api_migracion_documentos.Domain.Repository;
using api_migracion_documentos.Domain.UseCase;
using api_migracion_documentos.Infraestructure.Persistence;
using api_migracion_documentos.Infraestructure.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders;

namespace api_migracion_documentos.Application.UseCaseImpl
{
    /// <summary>
    /// Orquesta el dashboard de migración: port de frontend/app.py (Flask).
    /// Toda la data pasa por SPs sp_mig_*; el servicio migrador sigue siendo
    /// el proceso externo lanzado por RunManager.
    /// </summary>
    public class MigracionUseCase : IMigracionUseCase
    {
        private readonly ConexionFactory _conexiones;
        private readonly ServicioFiles _files;
        private readonly RunManager _runs;
        private readonly IMigracionMsRepository _ms;
        private readonly IMigracionSpringRepository _spring;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = null,
            WriteIndented = false
        };

        public MigracionUseCase(
            ConexionFactory conexiones,
            ServicioFiles files,
            RunManager runs,
            IMigracionMsRepository ms,
            IMigracionSpringRepository spring)
        {
            _conexiones = conexiones;
            _files = files;
            _runs = runs;
            _ms = ms;
            _spring = spring;
        }

        // ---------- helpers ----------

        private static JsonObject Ok() => new() { ["ok"] = true };
        private static ApiResult Fail(string error, int status = 500) =>
            new(new JsonObject { ["ok"] = false, ["error"] = error }, status);

        private static string? Str(JsonElement el, string prop)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null
                ? v.ToString() : null;

        private static string? GetStr(Dictionary<string, object?> cfg, string key)
            => cfg.TryGetValue(key, out var v) ? v?.ToString() : null;

        private static Dictionary<string, object?> CfgFromJson(JsonElement data)
        {
            var cfg = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in data.EnumerateObject())
                cfg[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString();
            return cfg;
        }

        /// <summary>Equivalente a get_activo_cfg(): config del entorno activo (DB) o fallback a config_modo.bat.</summary>
        private async Task<Dictionary<string, object?>> GetActivoCfgAsync()
        {
            try
            {
                var cfg = await _conexiones.ObtenerConfigActivaAsync();
                if (cfg != null) return cfg;
            }
            catch { }
            var baseCfg = _files.ParseConfigModo()
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);
            baseCfg["ENTORNO"] = "PRUEBA";
            return baseCfg;
        }

        private SqlConnection ConnMs(Dictionary<string, object?> cfg)
            => _conexiones.CrearMs(cfg);

        private SqlConnection ConnSpring(Dictionary<string, object?> cfg)
            => _conexiones.CrearSpring(cfg);

        private static string JsonSerialize(object value) => JsonSerializer.Serialize(value, JsonOpts);

        private static JsonElement ArrayToElement(IEnumerable<object> list)
            => JsonDocument.Parse(JsonSerializer.Serialize(list, JsonOpts)).RootElement;

        // ---------- entornos / config ----------

        public async Task<ApiResult> EntornosAsync()
        {
            try
            {
                await using var conn = _conexiones.CrearMsBootstrap();
                var data = await _ms.EntornosAsync(conn);
                var body = Ok();
                body["entornos"] = JsonNode.Parse(data.GetProperty("entornos").GetRawText());
                body["activo"] = data.GetProperty("activo").GetString();
                return new ApiResult(body);
            }
            catch (Exception)
            {
                var body = Ok();
                body["entornos"] = JsonNode.Parse(
                    "[{\"Entorno\":\"PRUEBA\",\"Descripcion\":\"Pruebas (fallback)\",\"ProduccionHabilitado\":false}]");
                body["activo"] = "PRUEBA";
                return new ApiResult(body);
            }
        }

        public async Task<ApiResult> CambiarEntornoAsync(string entorno, string ip)
        {
            if (string.IsNullOrWhiteSpace(entorno)) return Fail("Falta entorno", 400);
            try
            {
                await using var conn = _conexiones.CrearMsBootstrap();
                var data = await _ms.EntornoActivoAsync(conn,
                    JsonSerialize(new { accion = "S", entorno, usuario = "api", ip }));

                // Regenerar archivos con la config del nuevo entorno
                var cfgData = await _ms.ConfigAsync(conn, JsonSerialize(new { accion = "G", entorno }));
                _files.RegenerarArchivos(CfgFromJson(cfgData));

                var body = Ok();
                body["entorno_activo"] = data.GetProperty("entorno_activo").GetString();
                return new ApiResult(body);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("no habilitada"))
            {
                return Fail(ex.Message, 403);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("no encontrado"))
            {
                return Fail(ex.Message, 404);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        public async Task<ApiResult> ConfigGetAsync()
        {
            var entornos = JsonNode.Parse("[]");
            var activo = "PRUEBA";
            try
            {
                await using var conn = _conexiones.CrearMsBootstrap();
                var data = await _ms.EntornosAsync(conn);
                entornos = JsonNode.Parse(data.GetProperty("entornos").GetRawText());
                activo = data.GetProperty("activo").GetString() ?? "PRUEBA";
            }
            catch { }

            var cfg = await GetActivoCfgAsync();
            var body = Ok();
            body["entorno_activo"] = activo;
            body["entornos"] = entornos;
            body["config"] = JsonNode.Parse(JsonSerialize(cfg));
            return new ApiResult(body);
        }

        public async Task<ApiResult> ConfigUpdateAsync(JsonElement updates)
        {
            var cfgObj = await GetActivoCfgAsync();
            var entorno = GetStr(cfgObj, "ENTORNO") ?? "PRUEBA";

            // merge updates sobre la config activa (solo claves conocidas)
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in cfgObj)
                if (kv.Value != null) merged[kv.Key] = kv.Value.ToString()!;
            foreach (var p in updates.EnumerateObject())
                if (merged.ContainsKey(p.Name) || _files.ClavesConfigurables.Contains(p.Name))
                    merged[p.Name] = p.Value.ToString();

            var errs = _files.ValidarConfig(merged);
            if (errs.Count > 0)
                return new ApiResult(new JsonObject
                {
                    ["ok"] = false,
                    ["errors"] = JsonNode.Parse(JsonSerialize(errs))
                }, 400);

            try
            {
                await using var conn = _conexiones.CrearMsBootstrap();
                await _ms.ConfigAsync(conn, JsonSerialize(new { accion = "U", entorno, data = merged }));
                var cfgData = await _ms.ConfigAsync(conn, JsonSerialize(new { accion = "G", entorno }));
                _files.RegenerarArchivos(CfgFromJson(cfgData));
                var body = Ok();
                body["entorno_activo"] = entorno;
                return new ApiResult(body);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        // ---------- stats ----------

        public async Task<ApiResult> StatsAsync()
        {
            var cfg = await GetActivoCfgAsync();
            try
            {
                await using var connMs = ConnMs(cfg);
                await using var connSpring = ConnSpring(cfg);

                var stats = await _ms.StatsAsync(connMs);
                var gd = await _spring.GdPruebaCountAsync(connSpring);

                int Int(string k) => stats.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                JsonNode? Node(string k) => stats.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
                    ? JsonNode.Parse(v.GetRawText()) : JsonNode.Parse("[]");

                var body = Ok();
                body["ap"] = JsonNode.Parse(JsonSerialize(new
                {
                    total = Int("ap_total"),
                    with_data = Int("ap_with_data"),
                    pending = Int("ap_pending"),
                    done = Int("ap_done")
                }));
                body["ap"]!.AsObject()["by_gatipo"] = Node("ap_by_gatipo");
                body["ap"]!.AsObject()["by_estado"] = Node("ap_by_estado");
                body["ms"] = JsonNode.Parse(JsonSerialize(new
                {
                    total = Int("ms_total"),
                    pending = Int("ms_pending"),
                    done = Int("ms_done")
                }));
                body["ms"]!.AsObject()["by_estado"] = Node("ms_by_estado");
                if (stats.TryGetProperty("last_run", out var lr) && lr.ValueKind != JsonValueKind.Null)
                    body["last_run"] = lr.GetString();
                body["gd_test"] = gd.GetProperty("total").GetInt32();
                body["configured_batch"] = JsonNode.Parse(JsonSerialize(new
                {
                    BATCH_PRUEBA = int.TryParse(GetStr(cfg, "BATCH_PRUEBA"), out var b1) ? b1 : 0,
                    BATCH_AP_DOCUMENTO_PRUEBA = int.TryParse(GetStr(cfg, "BATCH_AP_DOCUMENTO_PRUEBA"), out var b2) ? b2 : 0
                }));
                return new ApiResult(body);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        // ---------- archivos ----------

        public async Task<ApiResult> ArchivosAsync(JsonElement filtros)
        {
            var cfg = await GetActivoCfgAsync();
            var tipo = (Str(filtros, "tipo") ?? "ap").ToLowerInvariant();
            var estado = Str(filtros, "estado");
            var gatipo = Str(filtros, "gatipo");
            var referencia = (Str(filtros, "ref") ?? "").Trim().ToUpperInvariant();
            if (referencia is not ("OC" or "SO")) referencia = null!;
            var page = Math.Max(1, int.TryParse(Str(filtros, "page"), out var p) ? p : 1);
            var perPage = Math.Clamp(int.TryParse(Str(filtros, "per_page"), out var pp) ? pp : 25, 1, 200);

            try
            {
                await using var connMs = ConnMs(cfg);

                if (tipo == "ap")
                {
                    if (referencia != null)
                    {
                        // Filtro OC/OS: cruzar claves contra AP_Documentos (Spring)
                        var cand = await _ms.CandidatosApAsync(connMs,
                            JsonSerialize(new { estado, gatipo }));
                        var candidatos = cand.EnumerateArray()
                            .Select(c => new
                            {
                                id = c.GetProperty("id").GetInt32(),
                                p = c.GetProperty("p").GetString() ?? "",
                                t = c.GetProperty("t").GetString() ?? "",
                                n = c.GetProperty("n").GetString() ?? ""
                            }).ToList();

                        var claves = candidatos
                            .Select(c => new { c.p, c.t, c.n })
                            .Where(c => !(c.p == "" && c.t == "" && c.n == ""))
                            .Distinct().ToList();

                        // Resolver en Spring por lotes de 150
                        var clavesOk = new HashSet<string>();
                        await using var connSpring = ConnSpring(cfg);
                        for (var i = 0; i < claves.Count; i += 150)
                        {
                            var grupo = claves.Skip(i).Take(150).ToList();
                            var res = await _spring.ClavesPorRefAsync(connSpring,
                                JsonSerialize(new { refTipo = referencia, claves = grupo }));
                            foreach (var c in res.EnumerateArray())
                                clavesOk.Add($"{c.GetProperty("p").GetString()}|{c.GetProperty("t").GetString()}|{c.GetProperty("n").GetString()}");
                        }

                        var ids = candidatos
                            .Where(c => clavesOk.Contains($"{c.p}|{c.t}|{c.n}"))
                            .Select(c => c.id)
                            .OrderByDescending(x => x)
                            .ToList();
                        var total = ids.Count;
                        var pageIds = ids.Skip((page - 1) * perPage).Take(perPage).ToList();

                        JsonArray rows;
                        if (pageIds.Count == 0)
                        {
                            rows = new JsonArray();
                        }
                        else
                        {
                            var rowsData = await _ms.ArchivosApAsync(connMs, JsonSerialize(new { ids = pageIds }));
                            rows = (JsonArray)JsonNode.Parse(rowsData.GetRawText())!;
                        }
                        await EnriquecerReferenciasAsync(cfg, rows);

                        var body = Ok();
                        body["rows"] = rows;
                        body["total"] = total;
                        body["page"] = page;
                        body["per_page"] = perPage;
                        return new ApiResult(body);
                    }

                    var data = await _ms.ArchivosApAsync(connMs,
                        JsonSerialize(new { estado, gatipo, page, per_page = perPage }));
                    var rowsNode = (JsonArray)JsonNode.Parse(data.GetProperty("rows").GetRawText())!;
                    await EnriquecerReferenciasAsync(cfg, rowsNode);

                    var bodyAp = Ok();
                    bodyAp["rows"] = rowsNode;
                    bodyAp["total"] = data.GetProperty("total").GetInt32();
                    bodyAp["page"] = page;
                    bodyAp["per_page"] = perPage;
                    return new ApiResult(bodyAp);
                }
                else
                {
                    var data = await _ms.ArchivosMsAsync(connMs,
                        JsonSerialize(new { estado, page, per_page = perPage }));
                    var body = Ok();
                    body["rows"] = JsonNode.Parse(data.GetProperty("rows").GetRawText());
                    body["total"] = data.GetProperty("total").GetInt32();
                    body["page"] = page;
                    body["per_page"] = perPage;
                    return new ApiResult(body);
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("resolver el filtro"))
            {
                return Fail(ex.Message, 502);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>Agrega "Referencias" (OC/OS) a cada fila AP, como _referencias_ap de Flask.</summary>
        private async Task EnriquecerReferenciasAsync(Dictionary<string, object?> cfg, JsonArray rows)
        {
            if (rows.Count == 0) return;
            var claves = rows.OfType<JsonObject>()
                .Select(r => new
                {
                    p = (r["Proveedor"]?.GetValue<string>() ?? "").Trim(),
                    t = (r["ObligacionTipoDocumento"]?.GetValue<string>() ?? "").Trim(),
                    n = (r["ObligacionNumeroDocumento"]?.GetValue<string>() ?? "").Trim()
                })
                .Where(c => !(c.p == "" && c.t == "" && c.n == ""))
                .Distinct().ToList();
            if (claves.Count == 0) return;

            try
            {
                await using var connSpring = ConnSpring(cfg);
                var refMap = new Dictionary<string, SortedSet<string>>();
                for (var i = 0; i < claves.Count; i += 150)
                {
                    var grupo = claves.Skip(i).Take(150).ToList();
                    var res = await _spring.ReferenciasAsync(connSpring, JsonSerialize(new { claves = grupo }));
                    foreach (var c in res.EnumerateArray())
                    {
                        var key = $"{c.GetProperty("p").GetString()}|{c.GetProperty("t").GetString()}|{c.GetProperty("n").GetString()}";
                        var refTipo = c.GetProperty("refTipo").GetString();
                        var refNum = c.GetProperty("refNum").GetString();
                        if (!string.IsNullOrEmpty(refTipo) && !string.IsNullOrEmpty(refNum))
                        {
                            if (!refMap.TryGetValue(key, out var set)) refMap[key] = set = new SortedSet<string>();
                            set.Add($"{refTipo} {refNum}");
                        }
                    }
                }
                foreach (var r in rows.OfType<JsonObject>())
                {
                    var key = $"{(r["Proveedor"]?.GetValue<string>() ?? "").Trim()}|{(r["ObligacionTipoDocumento"]?.GetValue<string>() ?? "").Trim()}|{(r["ObligacionNumeroDocumento"]?.GetValue<string>() ?? "").Trim()}";
                    r["Referencias"] = refMap.TryGetValue(key, out var set) ? string.Join(", ", set) : "";
                }
            }
            catch
            {
                foreach (var r in rows.OfType<JsonObject>()) r["Referencias"] ??= "";
            }
        }

        // ---------- previews ----------

        public async Task<(byte[] datos, string nombre, string mime)?> PreviewApAsync(int id)
        {
            var cfg = await GetActivoCfgAsync();
            await using var conn = ConnMs(cfg);
            var rows = await _ms.PreviewApAsync(conn, JsonSerialize(new { id }));
            if (rows.Count == 0) return null;
            var row = rows[0];
            var nombre = row["Ganombre"]?.ToString() ?? "archivo.bin";
            var datos = row["Gadatos"] switch
            {
                byte[] b => b,
                null => [],
                _ => []
            };
            return (datos, nombre, MimePreview(nombre, null));
        }

        public async Task<(byte[] datos, string nombre, string mime)?> PreviewMsAsync(string id)
        {
            var cfg = await GetActivoCfgAsync();
            await using var conn = ConnMs(cfg);
            var rows = await _ms.PreviewMsAsync(conn, JsonSerialize(new { id }));
            if (rows.Count == 0) return null;
            var row = rows[0];
            var nombre = row["Nombre"]?.ToString() ?? "archivo.bin";
            var estado = row["Estado"]?.ToString();
            var ruta = row["RutaDestino"]?.ToString();
            byte[] datos = row["Contenido"] as byte[] ?? [];
            if (estado == "COMPLETADO" && !string.IsNullOrEmpty(ruta) && File.Exists(ruta))
            {
                try { datos = await File.ReadAllBytesAsync(ruta); } catch { }
            }
            if (datos.Length == 0) return null;
            return (datos, nombre, MimePreview(nombre, row["Tipo"]?.ToString()));
        }

        private static string MimePreview(string nombre, string? mime)
        {
            var m = mime;
            if (string.IsNullOrEmpty(m))
            {
                m = Path.GetExtension(nombre).ToLowerInvariant() switch
                {
                    ".pdf" => "application/pdf",
                    ".xml" => "application/xml",
                    ".zip" => "application/zip",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".txt" => "text/plain",
                    ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    _ => "application/octet-stream"
                };
            }
            if (m.Contains("/xml") || m.EndsWith("+xml")) return "text/plain";
            return m;
        }

        // ---------- log ----------

        public Task<ApiResult> LogAsync(int tail)
        {
            try
            {
                var body = Ok();
                body["lines"] = JsonNode.Parse(JsonSerialize(_files.LogTail(tail)));
                return Task.FromResult(new ApiResult(body));
            }
            catch (Exception ex)
            {
                return Task.FromResult(Fail(ex.Message));
            }
        }

        // ---------- ejecuciones ----------

        private ApiResult? ValidarEjecucion(Dictionary<string, object?> cfg)
        {
            var modo = (GetStr(cfg, "MODO_SERVICIO") ?? "").ToUpperInvariant();
            if (modo != "PRUEBA")
                return Fail("Solo se puede ejecutar en modo PRUEBA desde la API", 403);
            var cfgStr = cfg.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
            var errs = _files.ValidarConfig(cfgStr);
            if (errs.Count > 0)
                return new ApiResult(new JsonObject
                {
                    ["ok"] = false,
                    ["errors"] = JsonNode.Parse(JsonSerialize(errs))
                }, 400);
            return null;
        }

        public async Task<ApiResult> RunAsync()
        {
            var cfg = await GetActivoCfgAsync();
            if (ValidarEjecucion(cfg) is { } err) return err;
            var rid = _runs.Start(_files.IniciadorPath, "Ejecución manual");
            var body = Ok();
            body["run_id"] = rid;
            body["name"] = "Ejecución manual";
            return new ApiResult(body);
        }

        public async Task<ApiResult> RunAllAsync()
        {
            var cfg = await GetActivoCfgAsync();
            if (ValidarEjecucion(cfg) is { } err) return err;
            var entorno = GetStr(cfg, "ENTORNO") ?? "PRUEBA";
            try
            {
                await using var conn = ConnMs(cfg);
                var pend = await _ms.PendientesAsync(conn);
                var msPending = pend.GetProperty("ms").GetInt32();
                var apPending = pend.GetProperty("ap").GetInt32();

                await _ms.ActualizarLotesAsync(conn, JsonSerialize(new
                {
                    entorno,
                    BATCH_PRUEBA = Math.Max(1, msPending).ToString(),
                    BATCH_AP_DOCUMENTO_PRUEBA = Math.Max(1, apPending).ToString()
                }));
                var cfgData = await _ms.ConfigAsync(conn, JsonSerialize(new { accion = "G", entorno }));
                _files.RegenerarArchivos(CfgFromJson(cfgData));

                var rid = _runs.Start(_files.IniciadorPath, "Procesar todo lo faltante");
                var body = Ok();
                body["run_id"] = rid;
                body["name"] = "Procesar todo lo faltante";
                body["pending_ms"] = msPending;
                body["pending_ap"] = apPending;
                return new ApiResult(body);
            }
            catch (Exception ex)
            {
                return Fail($"No se pudo contar pendientes: {ex.Message}");
            }
        }

        public async Task<ApiResult> RunOrdenAsync(string numero, string? tipo)
        {
            var cfg = await GetActivoCfgAsync();
            if (ValidarEjecucion(cfg) is { } err) return err;

            numero = (numero ?? "").Trim();
            tipo = string.IsNullOrWhiteSpace(tipo) ? null : tipo.Trim().ToUpperInvariant();
            if (!Regex.IsMatch(numero, @"^[0-9A-Za-z\-]{1,30}$"))
                return Fail("Número de orden inválido (use dígitos, letras o guiones)", 400);
            if (tipo is not (null or "OC" or "SO"))
                return Fail("Tipo debe ser OC u OS", 400);

            try
            {
                await using var connSpring = ConnSpring(cfg);
                await using var connMs = ConnMs(cfg);

                var oblData = await _spring.ObligacionesReferenciaAsync(connSpring,
                    JsonSerialize(new { numero, tipo }));
                var obligaciones = oblData.EnumerateArray()
                    .Select(c => new
                    {
                        p = c.GetProperty("p").GetString() ?? "",
                        t = c.GetProperty("t").GetString() ?? "",
                        n = c.GetProperty("n").GetString() ?? ""
                    }).ToList();

                var pend = await _ms.PendientesOrdenAsync(connMs,
                    JsonSerialize(new { numero, tipo, obligaciones }));
                var total = pend.GetProperty("total").GetInt32();
                var ap = pend.GetProperty("ap").GetInt32();
                var ms = pend.GetProperty("ms").GetInt32();

                if (total == 0)
                    return Fail(
                        $"No hay archivos pendientes para la orden {numero} " +
                        $"(ya migrados o sin adjuntos nuevos). Obligaciones relacionadas: {obligaciones.Count}", 404);

                var env = new Dictionary<string, string> { ["SOLO_REFERENCIA"] = numero };
                if (tipo != null) env["SOLO_REFERENCIA_TIPO"] = tipo;

                var rid = _runs.Start(_files.IniciadorPath,
                    $"Procesar orden {tipo ?? "OC/OS"} {numero}", env);
                var body = Ok();
                body["run_id"] = rid;
                body["name"] = $"Orden {numero}";
                body["obligaciones"] = obligaciones.Count;
                body["pending_ap"] = ap;
                body["pending_ms"] = ms;
                return new ApiResult(body);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        public ApiResult RunStatus(string runId)
        {
            var info = _runs.Get(runId);
            if (info == null) return Fail("Ejecución no encontrada", 404);
            var body = Ok();
            body["id"] = info.id;
            body["name"] = info.name;
            body["status"] = info.status;
            body["exit_code"] = info.exit_code;
            body["started"] = info.started;
            body["finished"] = info.finished;
            string[] output;
            lock (info.output) output = info.output.ToArray();
            body["output"] = JsonNode.Parse(JsonSerialize(output));
            return new ApiResult(body);
        }
    }
}
