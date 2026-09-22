using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace api_migracion_documentos.Infraestructure.Persistence
{
    /// <summary>
    /// Resuelve las cadenas de conexión dinámicamente según el entorno activo
    /// (tabla dbo.Entornos_Migracion / dbo.Config_Activo en el portal), igual
    /// que get_activo_cfg() del dashboard Flask. La clave nunca se guarda en
    /// appsettings: se toma de la variable de entorno SQL_PASSWORD.
    /// </summary>
    public class ConexionFactory
    {
        private readonly IConfiguration _configuration;
        private readonly string _rutaServicio;

        public ConexionFactory(IConfiguration configuration)
        {
            _configuration = configuration;
            _rutaServicio = configuration["Migracion:RutaServicio"]!;
        }

        private string Password => Environment.GetEnvironmentVariable("SQL_PASSWORD")
            ?? throw new InvalidOperationException("Falta la variable de entorno SQL_PASSWORD");

        private static string BuildCs(string? server, string? db, string? user, string password)
        {
            var csb = new SqlConnectionStringBuilder
            {
                DataSource = server ?? "",
                InitialCatalog = db ?? "",
                UserID = user ?? "",
                Password = password,
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 15
            };
            return csb.ConnectionString;
        }

        /// <summary>Parsea config_modo.bat (set "K=V") como base de arranque.</summary>
        public Dictionary<string, string> ParseConfigModo()
        {
            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(_rutaServicio, _configuration["Migracion:ConfigModo"]!);
            if (!File.Exists(path)) return cfg;
            foreach (var raw in File.ReadAllLines(path))
            {
                var m = Regex.Match(raw, @"^set\s+""([^""]+)=([^""]*)""\s*$");
                if (m.Success) cfg[m.Groups[1].Value] = m.Groups[2].Value;
            }
            return cfg;
        }

        /// <summary>Conexión al portal usando appsettings como base.</summary>
        public SqlConnection CrearMsBootstrap()
        {
            var cs = _configuration.GetConnectionString("MsHass")!;
            var csb = new SqlConnectionStringBuilder(cs) { Password = Password };
            return new SqlConnection(csb.ConnectionString);
        }

        /// <summary>
        /// Config activa: lee Config_Activo + Entornos_Migracion desde el portal.
        /// Devuelve la fila del entorno o null si no hay conexión.
        /// </summary>
        public async Task<Dictionary<string, object?>?> ObtenerConfigActivaAsync()
        {
            await using var conn = CrearMsBootstrap();
            await conn.OpenAsync();

            string entorno = "PRUEBA";
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT TOP 1 Entorno FROM dbo.Config_Activo";
                var r = await cmd.ExecuteScalarAsync();
                if (r != null) entorno = r.ToString() ?? "PRUEBA";
            }

            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "SELECT * FROM dbo.Entornos_Migracion WHERE Entorno = @e";
            cmd2.Parameters.AddWithValue("@e", entorno);
            await using var rd = await cmd2.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;
            var cfg = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rd.FieldCount; i++)
                cfg[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
            cfg["ENTORNO"] = entorno;
            return cfg;
        }

        /// <summary>Conexión al portal según el entorno activo.</summary>
        public SqlConnection CrearMs(Dictionary<string, object?> cfg)
        {
            return new SqlConnection(BuildCs(
                cfg.GetValueOrDefault("SERVIDOR_MS_HASS")?.ToString(),
                cfg.GetValueOrDefault("BD_MS_HASS")?.ToString(),
                cfg.GetValueOrDefault("SQL_USER")?.ToString(), Password));
        }

        /// <summary>Conexión a Spring (prueba o producción según entorno activo).</summary>
        public SqlConnection CrearSpring(Dictionary<string, object?> cfg)
        {
            return new SqlConnection(BuildCs(
                cfg.GetValueOrDefault("SERVIDOR_SPRING")?.ToString(),
                cfg.GetValueOrDefault("BD_SPRING")?.ToString(),
                cfg.GetValueOrDefault("SQL_USER")?.ToString(), Password));
        }
    }
}
