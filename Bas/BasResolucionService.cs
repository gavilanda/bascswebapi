using System.Text.Json;

namespace PortalClientes.Bas;

// Resultado de resolver un código en una base. Para productos, Articulo trae
// los datos del Bien (unidad, relación, partidas/series); para proveedores es null.
public record ResolucionBase(string Base, bool Existe, string? Descripcion, string? Error, BienInfo? Articulo = null);

// Resuelve códigos de producto/proveedor en todas las bases BAS a la vez.
// Estrategia:
//  - Si el padrón de ese maestro/base ESTÁ cargado: confiamos en él. El código
//    está (✓) o no está (✗ "no existe"); NO consultamos en vivo.
//  - Si el padrón TODAVÍA no está cargado (calentando): consulta en vivo por
//    código, con tope corto (8s) para no colgar.
// Las bases se consultan en paralelo.
public class BasResolucionService
{
    private readonly BasDestinosService _destinos;
    private readonly BasCacheMaestros _cache;

    private static readonly TimeSpan TopeEnVivo = TimeSpan.FromSeconds(8);

    public BasResolucionService(BasDestinosService destinos, BasCacheMaestros cache)
    {
        _destinos = destinos;
        _cache = cache;
    }

    public async Task<List<ResolucionBase>> ResolverProductoAsync(string codigo, CancellationToken ct = default)
    {
        var cod = codigo.Trim();
        var tareas = _destinos.Nombres.Select(async destino =>
        {
            var snap = _cache.Obtener(destino);
            if (snap.BienesListo)
            {
                if (snap.Bienes.TryGetValue(cod, out var info))
                    return new ResolucionBase(destino, true, Desc(info.Descripcion), null, info);
                return new ResolucionBase(destino, false, null, null, null);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TopeEnVivo);
            try
            {
                var json = await _destinos.GetAsync(destino, "/api/Bienes/" + Uri.EscapeDataString(cod), cts.Token);
                if (string.IsNullOrWhiteSpace(json)) return new ResolucionBase(destino, false, null, null, null);
                using var doc = JsonDocument.Parse(json);
                var info = BienInfo.Desde(doc.RootElement);
                return new ResolucionBase(destino, true, Desc(info.Descripcion), null, info);
            }
            catch (OperationCanceledException)
            {
                return new ResolucionBase(destino, false, null, "la base no respondió a tiempo", null);
            }
            catch (Exception ex)
            {
                return new ResolucionBase(destino, false, null, ex.Message, null);
            }
        });

        var resultados = await Task.WhenAll(tareas);
        return resultados.ToList();
    }

    public async Task<List<ResolucionBase>> ResolverProveedorAsync(string codigo, CancellationToken ct = default)
    {
        var cod = codigo.Trim();
        var tareas = _destinos.Nombres.Select(async destino =>
        {
            var snap = _cache.Obtener(destino);
            if (snap.ProveedoresListo)
            {
                if (snap.Proveedores.TryGetValue(cod, out var rs))
                    return new ResolucionBase(destino, true, Desc(rs), null, null);
                return new ResolucionBase(destino, false, null, null, null);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TopeEnVivo);
            try
            {
                var json = await _destinos.GetAsync(destino, "/api/Proveedores/" + Uri.EscapeDataString(cod), cts.Token);
                if (string.IsNullOrWhiteSpace(json)) return new ResolucionBase(destino, false, null, null, null);
                using var doc = JsonDocument.Parse(json);
                var rs = BienInfo.Prop(doc.RootElement, "RazonSocial") ?? "";
                return new ResolucionBase(destino, true, Desc(rs), null, null);
            }
            catch (OperationCanceledException)
            {
                return new ResolucionBase(destino, false, null, "la base no respondió a tiempo", null);
            }
            catch (Exception ex)
            {
                return new ResolucionBase(destino, false, null, ex.Message, null);
            }
        });

        var resultados = await Task.WhenAll(tareas);
        return resultados.ToList();
    }

    private static string? Desc(string s) => string.IsNullOrEmpty(s) ? null : s;
}
