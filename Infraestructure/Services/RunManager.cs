using System.Collections.Concurrent;
using System.Diagnostics;
using api_migracion_documentos.Domain.Models;

namespace api_migracion_documentos.Infraestructure.Services
{
    /// <summary>
    /// Lanza el servicio migrador como proceso hijo (cmd /c iniciador PRUEBA)
    /// y captura su salida línea a línea. Equivalente al RunManager de Flask.
    /// </summary>
    public class RunManager
    {
        private readonly ConcurrentDictionary<string, RunInfo> _runs = new();
        private int _counter;

        public string Start(string batPath, string name, IDictionary<string, string>? extraEnv = null,
            string modo = "PRUEBA")
        {
            var rid = $"run-{Interlocked.Increment(ref _counter)}";
            var info = new RunInfo { id = rid, name = name, started = DateTime.Now.ToString("o") };
            _runs[rid] = info;

            Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo("cmd.exe", $"/c \"{batPath}\" {modo}")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.Latin1,
                        StandardErrorEncoding = System.Text.Encoding.Latin1,
                        WorkingDirectory = Path.GetDirectoryName(batPath) ?? ""
                    };
                    if (extraEnv != null)
                        foreach (var kv in extraEnv) psi.Environment[kv.Key] = kv.Value;

                    using var p = Process.Start(psi)!;
                    var stdout = Task.Run(() =>
                    {
                        string? line;
                        while ((line = p.StandardOutput.ReadLine()) != null)
                            lock (info.output) info.output.Add(line);
                    });
                    var stderr = Task.Run(() =>
                    {
                        string? line;
                        while ((line = p.StandardError.ReadLine()) != null)
                            lock (info.output) info.output.Add(line);
                    });
                    p.WaitForExit();
                    Task.WaitAll(stdout, stderr);
                    info.exit_code = p.ExitCode;
                    info.status = p.ExitCode == 0 ? "success" : "error";
                }
                catch (Exception ex)
                {
                    lock (info.output) info.output.Add($"[ERROR] {ex.Message}");
                    info.status = "error";
                    info.exit_code = -1;
                }
                finally
                {
                    info.finished = DateTime.Now.ToString("o");
                }
            });

            return rid;
        }

        public RunInfo? Get(string rid) => _runs.TryGetValue(rid, out var info) ? info : null;
    }
}
