using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using api_migracion_documentos.Domain.Repository;
using api_migracion_documentos.Domain.UseCase;
using api_migracion_documentos.Infraestructure.Persistence;
using api_migracion_documentos.Infraestructure.Services;
using ClosedXML.Excel;
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

        /// <summary>"rows" del data de un SP listado; [] si el SP la omitio
        /// (FOR JSON omite claves NULL cuando la pagina queda vacia).</summary>
        private static JsonArray RowsProp(JsonElement data)
            => data.ValueKind == JsonValueKind.Object
               && data.TryGetProperty("rows", out var r)
               && r.ValueKind == JsonValueKind.Array
                ? (JsonArray)JsonNode.Parse(r.GetRawText())!
                : [];

        private static int IntProp(JsonElement data, string prop)
            => data.ValueKind == JsonValueKind.Object
               && data.TryGetProperty(prop, out var v)
               && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : 0;

        /// <summary>Elementos si el JsonElement es array; vacio en otro caso.</summary>
        private static IEnumerable<JsonElement> Arr(JsonElement el)
            => el.ValueKind == JsonValueKind.Array
                ? el.EnumerateArray() : Enumerable.Empty<JsonElement>();

        /// <summary>Propiedad array del data de un SP; [] si falta.</summary>
        private static JsonArray ArrProp(JsonElement data, string prop)
            => data.ValueKind == JsonValueKind.Object
               && data.TryGetProperty(prop, out var v)
               && v.ValueKind == JsonValueKind.Array
                ? (JsonArray)JsonNode.Parse(v.GetRawText())!
                : [];

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
                        var candidatos = Arr(cand)
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
                            foreach (var c in Arr(res))
                                clavesOk.Add($"{c.GetProperty("p").GetString()}|{c.GetProperty("t").GetString()}|{c.GetProperty("n").GetString()}");
                        }

                        var ids = candidatos
                            .Where(c => clavesOk.Contains($"{c.p}|{c.t}|{c.n}"))
                            .Select(c => c.id)
                            .OrderByDescending(x => x)
                            .ToList();
                        var total = ids.Count;
                        var pageIds = ids.Skip((page - 1) * perPage).Take(perPage).ToList();

                        var rowsData = await _ms.ArchivosApAsync(connMs,
                            JsonSerialize(new { ids = pageIds, gatipo }));
                        var rows = RowsProp(rowsData);
                        await EnriquecerReferenciasAsync(cfg, rows);

                        var body = Ok();
                        body["rows"] = rows;
                        body["total"] = total;
                        body["page"] = page;
                        body["per_page"] = perPage;
                        body["por_estado"] = ArrProp(rowsData, "por_estado");
                        return new ApiResult(body);
                    }

                    var data = await _ms.ArchivosApAsync(connMs,
                        JsonSerialize(new { estado, gatipo, page, per_page = perPage }));
                    var rowsNode = RowsProp(data);
                    await EnriquecerReferenciasAsync(cfg, rowsNode);

                    var bodyAp = Ok();
                    bodyAp["rows"] = rowsNode;
                    bodyAp["total"] = IntProp(data, "total");
                    bodyAp["page"] = page;
                    bodyAp["per_page"] = perPage;
                    bodyAp["por_estado"] = ArrProp(data, "por_estado");
                    return new ApiResult(bodyAp);
                }
                else
                {
                    var data = await _ms.ArchivosMsAsync(connMs,
                        JsonSerialize(new { estado, page, per_page = perPage }));
                    var rowsMs = RowsProp(data);
                    AplicarEmpresaMs(cfg, rowsMs);
                    var body = Ok();
                    body["rows"] = rowsMs;
                    body["total"] = IntProp(data, "total");
                    body["page"] = page;
                    body["per_page"] = perPage;
                    body["por_estado"] = ArrProp(data, "por_estado");
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

        /// <summary>Ultimo segmento de RUTA_RAIZ ("PRUEBAS" o "PROD").</summary>
        private static string SegmentoRaiz(Dictionary<string, object?> cfg)
        {
            var raiz = GetStr(cfg, "RUTA_RAIZ");
            var seg = raiz?.Replace('/', '\\')
                .Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return string.IsNullOrEmpty(seg) ? "PROD" : seg;
        }

        /// <summary>Empresa = segmento que sigue a la raiz en una ruta UNC
        /// (\\server\SpringGestionDoc\PRUEBAS\EMPRESA\...). Las rutas de
        /// AP_Documentos.RutaArchivo siempre usan el segmento PROD.</summary>
        private static string? EmpresaDeRuta(string? ruta, string segmentoRaiz)
        {
            if (string.IsNullOrWhiteSpace(ruta)) return null;
            var partes = ruta.Replace('/', '\\')
                .Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var idx = Array.FindIndex(partes,
                p => p.Equals(segmentoRaiz, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 && !segmentoRaiz.Equals("PROD", StringComparison.OrdinalIgnoreCase))
                idx = Array.FindIndex(partes, p => p.Equals("PROD", StringComparison.OrdinalIgnoreCase));
            return idx >= 0 && idx + 1 < partes.Length ? partes[idx + 1] : null;
        }

        /// <summary>Asigna "Empresa" a filas MS: RutaDestino (destino real) o la
        /// columna Empresa del portal si aun no se traslado.</summary>
        private static void AplicarEmpresaMs(Dictionary<string, object?> cfg, JsonArray rows)
        {
            var seg = SegmentoRaiz(cfg);
            foreach (var r in rows.OfType<JsonObject>())
                r["Empresa"] = EmpresaDeRuta(r["RutaDestino"]?.GetValue<string>(), seg)
                               ?? r["Empresa"]?.GetValue<string>() ?? "";
        }

        /// <summary>Excel (.xlsx) con los archivos en estado COMPLETADO del tipo dado.</summary>
        public async Task<(byte[]? datos, string nombre, string? error)> ArchivosExcelAsync(JsonElement filtros)
        {
            var cfg = await GetActivoCfgAsync();
            var tipo = (Str(filtros, "tipo") ?? "ap").ToLowerInvariant();
            var nombre = $"archivos_{tipo}_migrados_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
            try
            {
                await using var connMs = ConnMs(cfg);
                using var wb = new XLWorkbook();
                var esAp = tipo == "ap";
                var ws = wb.AddWorksheet(esAp ? "AP" : "MS");

                string[] headers = esAp
                    ? ["Id", "GATipo", "Nombre", "Empresa", "Proveedor", "TipoDoc",
                       "Obligacion", "OC/OS", "Estado", "Intentos", "FechaTraslado", "ArchivoId", "RutaDestino", "Carpeta"]
                    : ["IdArchivo", "Tabla", "IdRegistro", "Nombre", "Empresa",
                       "Estado", "Intentos", "FechaTraslado", "ArchivoId", "RutaDestino", "Carpeta"];
                string[] keys = esAp
                    ? ["IdApDocumentoArchivo", "Gatipo", "Ganombre", "Empresa", "Proveedor",
                       "ObligacionTipoDocumento", "ObligacionNumeroDocumento", "Referencias",
                       "Estado", "Intentos", "FechaTraslado", "ArchivoId", "RutaDestino"]
                    : ["IdArchivo", "Tabla", "IdRegistro", "Nombre", "Empresa",
                       "Estado", "Intentos", "FechaTraslado", "ArchivoId", "RutaDestino"];

                for (var c = 0; c < headers.Length; c++)
                {
                    ws.Cell(1, c + 1).Value = headers[c];
                    ws.Cell(1, c + 1).Style.Font.Bold = true;
                }

                var fila = 2;
                var page = 1;
                const int perPage = 200; // tope del SP
                while (true)
                {
                    JsonArray rows;
                    if (esAp)
                    {
                        var data = await _ms.ArchivosApAsync(connMs,
                            JsonSerialize(new { estado = "COMPLETADO", page, per_page = perPage }));
                        rows = RowsProp(data);
                        await EnriquecerReferenciasAsync(cfg, rows);
                    }
                    else
                    {
                        var data = await _ms.ArchivosMsAsync(connMs,
                            JsonSerialize(new { estado = "COMPLETADO", page, per_page = perPage }));
                        rows = RowsProp(data);
                        AplicarEmpresaMs(cfg, rows);
                    }
                    if (rows.Count == 0) break;

                    foreach (var r in rows.OfType<JsonObject>())
                    {
                        for (var c = 0; c < keys.Length; c++)
                            SetCellExcel(ws.Cell(fila, c + 1), r[keys[c]]);

                        // RutaDestino: link al archivo. Carpeta: la ruta de la grilla
                        // (termina en el nro. de OC/OS) con link al explorador.
                        var rutaDestino = r["RutaDestino"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(rutaDestino))
                        {
                            var ix = rutaDestino.Replace('/', '\\').LastIndexOf('\\');
                            var carpeta = ix > 0 ? rutaDestino[..ix] : rutaDestino;
                            HipervinculoUnc(ws.Cell(fila, keys.Length), rutaDestino, "Abrir archivo");
                            var celdaCarpeta = ws.Cell(fila, keys.Length + 1);
                            celdaCarpeta.Value = carpeta;
                            HipervinculoUnc(celdaCarpeta, carpeta, "Abrir carpeta en el explorador");
                        }
                        fila++;
                    }
                    if (rows.Count < perPage) break;
                    page++;
                }

                ws.Column(esAp ? 3 : 4).Width = 40;   // Nombre
                ws.Column(esAp ? 13 : 10).Width = 80; // RutaDestino
                ws.Column(esAp ? 14 : 11).Width = 70; // Carpeta

                using var ms = new MemoryStream();
                wb.SaveAs(ms);
                return (ms.ToArray(), nombre, null);
            }
            catch (Exception ex)
            {
                return (null, nombre, ex.Message);
            }
        }

        private static void HipervinculoUnc(IXLCell cell, string destino, string tooltip)
        {
            var link = cell.CreateHyperlink();
            link.ExternalAddress = new Uri(destino);
            link.Tooltip = tooltip;
            cell.Style.Font.FontColor = XLColor.Blue;
            cell.Style.Font.Underline = XLFontUnderlineValues.Single;
        }

        private static void SetCellExcel(IXLCell cell, JsonNode? node)
        {
            switch (node)
            {
                case null: cell.Value = ""; break;
                case JsonValue v when v.TryGetValue<long>(out var l): cell.Value = l; break;
                case JsonValue v when v.TryGetValue<double>(out var d): cell.Value = d; break;
                case JsonValue v when v.TryGetValue<bool>(out var b): cell.Value = b; break;
                case JsonValue v when v.TryGetValue<string>(out var s): cell.Value = s; break;
                default: cell.Value = node.ToString(); break;
            }
        }

        /// <summary>Agrega "Referencias" (OC/OS) y "Empresa" a cada fila AP, como _referencias_ap de Flask.</summary>
        private async Task EnriquecerReferenciasAsync(Dictionary<string, object?> cfg, JsonArray rows)
        {
            if (rows.Count == 0) return;
            var seg = SegmentoRaiz(cfg);
            foreach (var r in rows.OfType<JsonObject>())
                r["Empresa"] = EmpresaDeRuta(r["RutaDestino"]?.GetValue<string>(), seg) ?? "";

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
                var empMap = new Dictionary<string, string>();
                for (var i = 0; i < claves.Count; i += 150)
                {
                    var grupo = claves.Skip(i).Take(150).ToList();
                    var res = await _spring.ReferenciasAsync(connSpring, JsonSerialize(new { claves = grupo }));
                    foreach (var c in Arr(res))
                    {
                        var key = $"{c.GetProperty("p").GetString()}|{c.GetProperty("t").GetString()}|{c.GetProperty("n").GetString()}";
                        var refTipo = c.GetProperty("refTipo").GetString();
                        var refNum = c.GetProperty("refNum").GetString();
                        if (!string.IsNullOrEmpty(refTipo) && !string.IsNullOrEmpty(refNum))
                        {
                            if (!refMap.TryGetValue(key, out var set)) refMap[key] = set = new SortedSet<string>();
                            set.Add($"{refTipo} {refNum}");
                        }
                        if (!empMap.ContainsKey(key)
                            && c.TryGetProperty("ruta", out var rutaEl)
                            && rutaEl.ValueKind == JsonValueKind.String)
                        {
                            var emp = EmpresaDeRuta(rutaEl.GetString(), seg);
                            if (emp != null) empMap[key] = emp;
                        }
                    }
                }
                foreach (var r in rows.OfType<JsonObject>())
                {
                    var key = $"{(r["Proveedor"]?.GetValue<string>() ?? "").Trim()}|{(r["ObligacionTipoDocumento"]?.GetValue<string>() ?? "").Trim()}|{(r["ObligacionNumeroDocumento"]?.GetValue<string>() ?? "").Trim()}";
                    r["Referencias"] = refMap.TryGetValue(key, out var set) ? string.Join(", ", set) : "";
                    if ((r["Empresa"]?.GetValue<string>() ?? "") == ""
                        && empMap.TryGetValue(key, out var emp))
                        r["Empresa"] = emp;
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
                var obligaciones = Arr(oblData)
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
