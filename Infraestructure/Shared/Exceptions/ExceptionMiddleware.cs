namespace api_migracion_documentos.Infraestructure.Shared.Exceptions
{
    /// <summary>Mismo patrón que api_alquiler_maquinaria.</summary>
    public class ExceptionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionMiddleware> _logger;

        public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
                context.Response.ContentType = "application/json";
                var statusCode = ex switch
                {
                    ArgumentException => StatusCodes.Status400BadRequest,
                    UnauthorizedAccessException => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status500InternalServerError
                };
                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsJsonAsync(new { ok = false, error = ex.Message, status = statusCode });
            }
        }
    }
}
