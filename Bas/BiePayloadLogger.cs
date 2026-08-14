using System.Text;
using Microsoft.Extensions.Options;

namespace PortalClientes.Bas;

// Captura de payloads (request + response tal cual viajan) de las llamadas al Banco
// Credicoop, para la planilla de validación/homologación. Se activa con
// BancoBie:LogPayloads=true (por defecto APAGADO). Cada llamada se guarda como un archivo
// de texto legible en BancoBie:LogCarpeta, con timestamp + operación, y además se appendea
// a un único bie-payloads.log. Best-effort: si falla el disco, NO rompe la operación real.
public class BiePayloadLogger
{
    private readonly BancoBieOptions _opt;
    private readonly object _lock = new();

    public BiePayloadLogger(IOptions<BancoBieOptions> opt) => _opt = opt.Value;

    public bool Habilitado => _opt.LogPayloads;

    public void Registrar(string baseNombre, string operacion, string metodo, string url,
        string? requestBody, int status, string? responseBody)
    {
        if (!_opt.LogPayloads) return;
        try
        {
            var carpeta = string.IsNullOrWhiteSpace(_opt.LogCarpeta)
                ? @"C:\conciliacion\bie-payloads" : _opt.LogCarpeta.Trim();
            Directory.CreateDirectory(carpeta);

            var ahora = DateTime.Now;
            var opSafe = Limpiar(operacion);
            var baseSafe = Limpiar(baseNombre);
            var nombre = $"{ahora:yyyyMMdd-HHmmss-fff}_{baseSafe}_{opSafe}.txt";

            var sb = new StringBuilder();
            sb.AppendLine($"===== {operacion}  ({baseNombre}) =====");
            sb.AppendLine($"Fecha/hora : {ahora:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"HTTP       : {metodo} {url}");
            sb.AppendLine($"Status     : {status}");
            sb.AppendLine();
            sb.AppendLine("----- REQUEST -----");
            sb.AppendLine(string.IsNullOrWhiteSpace(requestBody) ? "(sin cuerpo)" : requestBody);
            sb.AppendLine();
            sb.AppendLine("----- RESPONSE -----");
            sb.AppendLine(string.IsNullOrWhiteSpace(responseBody) ? "(sin cuerpo)" : responseBody);
            sb.AppendLine();

            var texto = sb.ToString();
            lock (_lock)
            {
                File.WriteAllText(Path.Combine(carpeta, nombre), texto, new UTF8Encoding(false));
                File.AppendAllText(Path.Combine(carpeta, "bie-payloads.log"),
                    texto + new string('-', 70) + "\r\n\r\n", new UTF8Encoding(false));
            }
        }
        catch { /* best-effort: la captura nunca debe frenar la operación real */ }
    }

    private static string Limpiar(string? s)
    {
        var t = (s ?? "op").Trim();
        var invalidos = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t) sb.Append(invalidos.Contains(ch) ? '-' : ch);
        return sb.ToString();
    }
}
