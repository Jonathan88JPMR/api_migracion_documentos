using System.Text;
using System.Text.RegularExpressions;

namespace api_migracion_documentos.Infraestructure.Services
{
    /// <summary>
    /// Regenera config_modo.bat / config.env del servicio y lee el log,
    /// portado de frontend/app.py (parse/save_config_modo, parse_config_env,
    /// _regenerar_archivos, api_log, validate_config).
    /// </summary>
    public class ServicioFiles
    {
        private readonly IConfiguration _configuration;
        private readonly string _rutaServicio;

        private static readonly string[] EnvKeys =
        [
            "MODO_SERVICIO", "SERVIDOR_MS_HASS", "BD_MS_HASS", "SERVIDOR_SPRING", "BD_SPRING",
            "RUTA_PRUEBA_LOCAL", "RUTA_RAIZ", "TIPO_COPIA_PRUEBA", "MODO_PRUEBA",
            "BATCH_PRUEBA", "BATCH_AP_DOCUMENTO_PRUEBA", "TABLA_ADJUNTOS", "USUARIO_MIGRACION", "SQL_USER",
        ];

        public ServicioFiles(IConfiguration configuration)
        {
            _configuration = configuration;
            _rutaServicio = configuration["Migracion:RutaServicio"]!;
        }

        public string ConfigModoPath => Path.Combine(_rutaServicio, _configuration["Migracion:ConfigModo"]!);
        public string ConfigEnvPath => Path.Combine(_rutaServicio, _configuration["Migracion:ConfigEnv"]!);
        public string LogPath => Path.Combine(_rutaServicio, _configuration["Migracion:LogPrueba"]!);
        public string IniciadorPath => Path.Combine(_rutaServicio, _configuration["Migracion:Iniciador"]!);

        public Dictionary<string, string> ParseConfigModo()
        {
            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(ConfigModoPath)) return cfg;
            foreach (var raw in File.ReadAllLines(ConfigModoPath, Encoding.Latin1))
            {
                var m = Regex.Match(raw, @"^set\s+""([^""]+)=([^""]*)""\s*$");
                if (m.Success) cfg[m.Groups[1].Value] = m.Groups[2].Value;
            }
            return cfg;
        }

        public Dictionary<string, string> ParseConfigEnv()
        {
            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(ConfigEnvPath)) return cfg;
            foreach (var raw in File.ReadAllLines(ConfigEnvPath, Encoding.Latin1))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var idx = line.IndexOf('=');
                if (idx > 0) cfg[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }
            return cfg;
        }

        /// <summary>Regenera config_modo.bat (PRUEBA) o config.env (PRODUCCION) desde la fila del entorno.</summary>
        public void RegenerarArchivos(Dictionary<string, object?> cfg)
        {
            var modo = (cfg.GetValueOrDefault("MODO_SERVICIO")?.ToString() ?? "").ToUpperInvariant();
            string Get(string k) => cfg.GetValueOrDefault(k)?.ToString() ?? "";

            if (modo == "PRUEBA")
            {
                var sb = new StringBuilder("@echo off\n");
                foreach (var k in new[] { "MODO_SERVICIO", "TIPO_COPIA_PRUEBA", "MODO_PRUEBA",
                                          "BATCH_PRUEBA", "BATCH_AP_DOCUMENTO_PRUEBA", "SQL_USER",
                                          "SERVIDOR_MS_HASS", "BD_MS_HASS", "SERVIDOR_SPRING",
                                          "TABLA_ADJUNTOS", "USUARIO_MIGRACION" })
                    sb.AppendLine($"set \"{k}={Get(k)}\"");
                sb.AppendLine($"set \"BD_SPRING_PRUEBA={Get("BD_SPRING")}\"");
                sb.AppendLine($"set \"RUTA_PRUEBA_LOCAL={Get("RUTA_PRUEBA_LOCAL")}\"");
                sb.AppendLine($"set \"RUTA_RAIZ_PRUEBA={Get("RUTA_RAIZ")}\"");
                File.WriteAllText(ConfigModoPath, sb.ToString(), Encoding.Latin1);
            }
            else if (modo == "PRODUCCION")
            {
                var prodVal = cfg.GetValueOrDefault("ProduccionHabilitado");
                var habilitado = (prodVal is true)
                    || (prodVal is string s && s.Equals("true", StringComparison.OrdinalIgnoreCase))
                    ? "SI_PRODUCCION" : "NO";
                var sb = new StringBuilder("# Configuracion de produccion generada por api\n");
                sb.AppendLine("MODO_SERVICIO=PRODUCCION");
                sb.AppendLine($"HABILITAR_PRODUCCION={habilitado}");
                sb.AppendLine($"SERVIDOR_MS_HASS={Get("SERVIDOR_MS_HASS")}");
                sb.AppendLine($"BD_MS_HASS={Get("BD_MS_HASS")}");
                sb.AppendLine($"SERVIDOR_SPRING={Get("SERVIDOR_SPRING")}");
                sb.AppendLine($"BD_SPRING_PRODUCCION={Get("BD_SPRING")}");
                sb.AppendLine($"RUTA_RAIZ_PRODUCCION={Get("RUTA_RAIZ")}");
                sb.AppendLine($"SQL_USER={Get("SQL_USER")}");
                File.WriteAllText(ConfigEnvPath, sb.ToString(), Encoding.Latin1);
            }
        }

        /// <summary>Validación de configuración (port de validate_config de Flask).</summary>
        public List<string> ValidarConfig(Dictionary<string, string> cfg)
        {
            var errs = new List<string>();
            var modo = (cfg.GetValueOrDefault("MODO_SERVICIO") ?? "").ToUpperInvariant();
            if (modo == "PRUEBA")
            {
                if (!"HASS_SPRING_PRUEBA".Equals(cfg.GetValueOrDefault("BD_SPRING"), StringComparison.OrdinalIgnoreCase))
                    errs.Add("BD_SPRING debe ser HASS_SPRING_PRUEBA");
                if (cfg.GetValueOrDefault("RUTA_RAIZ") != @"\\172.16.20.24\SpringGestionDoc\PRUEBAS")
                    errs.Add("RUTA_RAIZ debe ser la ruta de pruebas");
            }
            else if (modo == "PRODUCCION")
            {
                if (!"HP_SPRING_PRD".Equals(cfg.GetValueOrDefault("BD_SPRING"), StringComparison.OrdinalIgnoreCase))
                    errs.Add("BD_SPRING debe ser HP_SPRING_PRD");
                if (cfg.GetValueOrDefault("RUTA_RAIZ") != @"\\172.16.20.24\SpringGestionDoc\PROD")
                    errs.Add("RUTA_RAIZ debe ser la ruta de producción");
            }
            else
            {
                errs.Add("MODO_SERVICIO debe ser PRUEBA o PRODUCCION");
            }
            var tipoCopia = (cfg.GetValueOrDefault("TIPO_COPIA_PRUEBA") ?? "").ToUpperInvariant();
            if (tipoCopia is not ("LOCAL" or "COMPARTIDA")) errs.Add("TIPO_COPIA_PRUEBA debe ser LOCAL o COMPARTIDA");
            var modoPrueba = (cfg.GetValueOrDefault("MODO_PRUEBA") ?? "").ToUpperInvariant();
            if (modoPrueba is not ("SIMULACION" or "ESCRITURA")) errs.Add("MODO_PRUEBA debe ser SIMULACION o ESCRITURA");
            foreach (var k in new[] { "BATCH_PRUEBA", "BATCH_AP_DOCUMENTO_PRUEBA" })
                if (!int.TryParse(cfg.GetValueOrDefault(k), out _)) errs.Add($"{k} debe ser numérico");
            return errs;
        }

        /// <summary>Tail del log (últimas N líneas, últimos 256 KB, latin-1).</summary>
        public List<string> LogTail(int tail)
        {
            tail = Math.Clamp(tail, 1, 500);
            if (!File.Exists(LogPath)) return [];
            var fi = new FileInfo(LogPath);
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(Math.Max(0, fi.Length - 256 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.Latin1);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            return lines.Count <= tail ? lines : lines[^tail..];
        }

        public IEnumerable<string> ClavesConfigurables => EnvKeys;
    }
}
