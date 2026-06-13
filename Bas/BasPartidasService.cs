using System.Globalization;
using System.Text.Json;

namespace PortalClientes.Bas;

// Alta idempotente de PARTIDAS (lotes) en BAS, contra /api/Partidas.
//
// BAS exige que la partida exista en dbo.PARTIDAS antes de usarla en el ítem de
// un ingreso (FK_MVSITEMS_PARTIDAS). Este servicio se asegura de que exista:
// consulta y, si falta, la crea. Crear una partida sin movimientos es inocuo
// (un lote con stock 0), así que ante una falla posterior del comprobante la
// dejamos: el próximo intento la reutiliza (idempotente).
//
// Endpoints BAS:
//   GET  /api/Partidas/{codigoItem}/{numero}   -> 200 si existe, 404 si no.
//   POST /api/Partidas  { CodigoItem, Numero, Descripcion, FechaVencimiento }.
public class BasPartidasService
{
    private readonly BasDestinosService _destinos;
    private readonly ILogger<BasPartidasService> _log;

    public BasPartidasService(BasDestinosService destinos, ILogger<BasPartidasService> log)
    {
        _destinos = destinos;
        _log = log;
    }

    public record EnsureResult(bool Ok, bool Creada, string? Error);

    // Número de partida estándar del portal: {códigoProveedor}-{ddMMyy de la fecha
    // del ingreso}. Se usa la fecha del ingreso (no "hoy") para que el reintento
    // sea idempotente: el mismo ingreso siempre genera la misma partida.
    public static string NumeroPartida(string proveedorCodigo, DateTime fechaIngreso)
        => $"{(proveedorCodigo ?? "").Trim()}-{fechaIngreso.ToString("ddMMyy", CultureInfo.InvariantCulture)}";

    // Garantiza que la partida (codigoItem, numero) exista en BAS. Si no existe,
    // la crea con la descripción y el vencimiento dados.
    public async Task<EnsureResult> AsegurarAsync(
        string destino, string codigoItem, string numero, string descripcion,
        DateTime fechaVencimiento, CancellationToken ct = default)
    {
        var ruta = $"/api/Partidas/{Uri.EscapeDataString(codigoItem)}/{Uri.EscapeDataString(numero)}";

        // 1) ¿Ya existe? (GET; un 404 hace que GetAsync devuelva null.)
        try
        {
            var existente = await _destinos.GetAsync(destino, ruta, ct);
            if (!string.IsNullOrWhiteSpace(existente))
                return new EnsureResult(true, false, null);
        }
        catch (Exception ex)
        {
            // Error real consultando (no un 404): no seguimos a ciegas.
            return new EnsureResult(false, false, $"No se pudo consultar la partida: {ex.Message}");
        }

        // 2) No existe -> crear.
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["CodigoItem"] = codigoItem,
            ["Numero"] = numero,
            ["Descripcion"] = descripcion,
            ["FechaVencimiento"] = fechaVencimiento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });

        try
        {
            await _destinos.PostAsync(destino, "/api/Partidas", body, ct);
            return new EnsureResult(true, true, null);
        }
        catch (Exception ex)
        {
            // Pudo haberse creado en paralelo (otro grabado simultáneo): reintentamos
            // el GET y, si ahora existe, lo damos por bueno.
            try
            {
                var existente2 = await _destinos.GetAsync(destino, ruta, ct);
                if (!string.IsNullOrWhiteSpace(existente2))
                    return new EnsureResult(true, false, null);
            }
            catch { /* ignore: devolvemos el error original */ }

            _log.LogWarning("Alta de partida {Numero} ({Item}) en {Base} falló: {Msg}",
                numero, codigoItem, destino, ex.Message);
            return new EnsureResult(false, false, ex.Message);
        }
    }
}
