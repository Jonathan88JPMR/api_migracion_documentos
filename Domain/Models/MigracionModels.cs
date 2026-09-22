namespace api_migracion_documentos.Domain.Models
{
    /// <summary>Clave de obligación (proveedor|tipo|numero) para cruzar con AP_Documentos.</summary>

    /// <summary>Clave de obligación (proveedor|tipo|numero) para cruzar con AP_Documentos.</summary>
    public record ClaveObligacion(string p, string t, string n);

    /// <summary>Resultado de una ejecución del servicio (equivalente al RunManager de Flask).</summary>
    public class RunInfo
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string status { get; set; } = "running"; // running | success | error
        public int? exit_code { get; set; }
        public List<string> output { get; } = new();
        public string started { get; set; } = "";
        public string? finished { get; set; }
    }
}
