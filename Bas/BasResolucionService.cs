using System.Text.Json;

namespace PortalClientes.Bas;

// Resultado de resolver un código en una base. Para productos, Articulo trae
// los datos del Bien (unidad, relación, partidas/series); para proveedores es null.
public record ResolucionBase(string Base, bool Existe, string? Descripcion, string? Error, BienInfo? Articulo = null);

// Resultado de validar un pre-remito completo contra una base concreta antes de
// conformar/grabar: dice si todo está y, si no, qué proveedor/artículos faltan.
public record ValidacionDestino(
    bool Ok,
    bool ProveedorExiste,
    string? ProveedorCodigo,
    List<string> ArticulosFaltantes,
    string? Error);

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

    // ---- Resolución contra UNA base concreta (para validar antes de conformar/grabar) ----

    // ¿Existe el proveedor en esta base? Usa el padrón si está cargado; si no,
    // consulta en vivo con tope corto.
    public async Task<ResolucionBase> ResolverProveedorEnBaseAsync(
        string destino, string codigo, CancellationToken ct = default)
    {
        var cod = codigo.Trim();
        var snap = _cache.Obtener(destino);
        if (snap.ProveedoresListo)
        {
            return snap.Proveedores.TryGetValue(cod, out var rs)
                ? new ResolucionBase(destino, true, Desc(rs), null, null)
                : new ResolucionBase(destino, false, null, null, null);
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
    }

    // ¿Existe el artículo en esta base? Devuelve también el BienInfo (sirve para
    // el cálculo de cantidades del grabado).
    public async Task<ResolucionBase> ResolverProductoEnBaseAsync(
        string destino, string codigo, CancellationToken ct = default)
    {
        var cod = codigo.Trim();
        var snap = _cache.Obtener(destino);
        if (snap.BienesListo)
        {
            return snap.Bienes.TryGetValue(cod, out var info)
                ? new ResolucionBase(destino, true, Desc(info.Descripcion), null, info)
                : new ResolucionBase(destino, false, null, null, null);
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
    }

    // Valida que un proveedor y una lista de códigos de artículo existan TODOS en
    // la base destino. Devuelve qué falta. Se usa al conformar (aviso temprano) y
    // se vuelve a usar al grabar (control duro, porque los códigos pueden cambiar).
    public async Task<ValidacionDestino> ValidarPreRemitoEnBaseAsync(
        string destino, string proveedorCodigo, IEnumerable<string> articulosCodigos, CancellationToken ct = default)
    {
        if (!_destinos.Existe(destino))
            return new ValidacionDestino(false, false, proveedorCodigo, new(), $"La base de destino '{destino}' no existe.");

        // Proveedor.
        var prov = await ResolverProveedorEnBaseAsync(destino, proveedorCodigo, ct);
        if (prov.Error is not null)
            return new ValidacionDestino(false, false, proveedorCodigo, new(), $"No se pudo verificar el proveedor: {prov.Error}");

        // Artículos: distintos, sin vacíos. Consultamos en paralelo.
        var codigos = articulosCodigos
            .Select(c => (c ?? "").Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var faltantes = new List<string>();
        var resultados = await Task.WhenAll(codigos.Select(c => ResolverProductoEnBaseAsync(destino, c, ct)));
        foreach (var (cod, res) in codigos.Zip(resultados))
        {
            if (res.Error is not null)
                return new ValidacionDestino(false, prov.Existe, proveedorCodigo, new(),
                    $"No se pudo verificar el artículo {cod}: {res.Error}");
            if (!res.Existe) faltantes.Add(cod);
        }

        var ok = prov.Existe && faltantes.Count == 0;
        return new ValidacionDestino(ok, prov.Existe, proveedorCodigo, faltantes, null);
    }

    private static string? Desc(string s) => string.IsNullOrEmpty(s) ? null : s;
}
