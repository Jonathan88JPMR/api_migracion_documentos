using System.Text.Json;
using api_migracion_documentos.Domain.UseCase;
using Microsoft.AspNetCore.Mvc;

namespace api_migracion_documentos.Infraestructure.Controller
{
    /// <summary>
    /// API del dashboard de migración de adjuntos.
    /// Convención POST + body JSON (como api_alquiler_maquinaria), salvo
    /// los previews binarios que son GET con FileResult.
    /// </summary>
    [ApiController]
    [Route("api/migracion-documentos")]
    public class MigracionController : ControllerBase
    {
        private readonly IMigracionUseCase _useCase;

        public MigracionController(IMigracionUseCase useCase)
        {
            _useCase = useCase;
        }

        private ObjectResult Responder(Domain.UseCase.ApiResult r)
            => StatusCode(r.Status, r.Body);

        private static JsonElement Body(JsonElement body) => body;

        [HttpPost("entornos")]
        public async Task<IActionResult> Entornos() => Responder(await _useCase.EntornosAsync());

        [HttpPost("cambiar-entorno")]
        public async Task<IActionResult> CambiarEntorno([FromBody] JsonElement body)
        {
            var entorno = Body(body).TryGetProperty("entorno", out var e) ? e.GetString() ?? "" : "";
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
            return Responder(await _useCase.CambiarEntornoAsync(entorno, ip));
        }

        [HttpPost("config-get")]
        public async Task<IActionResult> ConfigGet() => Responder(await _useCase.ConfigGetAsync());

        [HttpPost("config-update")]
        public async Task<IActionResult> ConfigUpdate([FromBody] JsonElement body)
        {
            var updates = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("updates", out var u)
                ? u : body;
            return Responder(await _useCase.ConfigUpdateAsync(updates));
        }

        [HttpPost("stats")]
        public async Task<IActionResult> Stats() => Responder(await _useCase.StatsAsync());

        [HttpPost("archivos")]
        public async Task<IActionResult> Archivos([FromBody] JsonElement body)
            => Responder(await _useCase.ArchivosAsync(body));

        [HttpGet("preview/ap/{id:int}")]
        public async Task<IActionResult> PreviewAp(int id)
        {
            var res = await _useCase.PreviewApAsync(id);
            if (res == null) return NotFound(new { ok = false, error = "No encontrado" });
            var (datos, nombre, mime) = res.Value;
            Response.Headers.ContentDisposition = $"inline; filename=\"{nombre}\"";
            return File(datos, mime);
        }

        [HttpGet("preview/ms/{id}")]
        public async Task<IActionResult> PreviewMs(string id)
        {
            var res = await _useCase.PreviewMsAsync(id);
            if (res == null) return NotFound(new { ok = false, error = "No encontrado" });
            var (datos, nombre, mime) = res.Value;
            Response.Headers.ContentDisposition = $"inline; filename=\"{nombre}\"";
            return File(datos, mime);
        }

        [HttpPost("log")]
        public async Task<IActionResult> Log([FromBody] JsonElement body)
        {
            var tail = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("tail", out var t)
                && t.TryGetInt32(out var n) ? n : 200;
            return Responder(await _useCase.LogAsync(tail));
        }

        [HttpPost("run")]
        public async Task<IActionResult> Run() => Responder(await _useCase.RunAsync());

        [HttpPost("run-all")]
        public async Task<IActionResult> RunAll() => Responder(await _useCase.RunAllAsync());

        [HttpPost("run-orden")]
        public async Task<IActionResult> RunOrden([FromBody] JsonElement body)
        {
            var numero = body.TryGetProperty("numero", out var n) ? n.GetString() ?? "" : "";
            var tipo = body.TryGetProperty("tipo", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            return Responder(await _useCase.RunOrdenAsync(numero, tipo));
        }

        [HttpPost("run-status")]
        public IActionResult RunStatus([FromBody] JsonElement body)
        {
            var rid = body.TryGetProperty("run_id", out var r) ? r.GetString() ?? "" : "";
            return Responder(_useCase.RunStatus(rid));
        }
    }
}
