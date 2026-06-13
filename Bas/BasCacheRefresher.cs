using System.Globalization;
using System.Text.Json;

namespace PortalClientes.Bas;

// Carga el padrón de productos (con datos del artículo) y proveedores de cada
// base y lo mantiene en BasCacheMaestros. Persiste en disco: al arrancar se lee
// del disco al instante; el refresco contra BAS ocurre en segundo plano y solo
// cuando está vencido.
//
// La carga usa el motor de consultas CONSULTAGRAL (POST /api/CONSULTAGRAL/{Entidad})
// pidiendo SOLO los campos que necesitamos (SelectDatosPrimarios). Eso hace la
// respuesta mucho más liviana que traer el objeto completo de cada registro.
// La carga es SECUENCIAL (un pedido por vez): el WebAPI de BAS no tolera varios
// paginados en paralelo. Singleton.
public class BasCacheRefresher
{
    private readonly BasDestinosService _destinos;
    private readonly BasCacheMaestros _cache;
    private readonly ILogger<BasCacheRefresher> _log;
    private readonly string _carpeta;

    private const int PageSize = 500;
    private const int MaxPaginas = 1000;
    private static readonly TimeSpan EdadMaximaPorDefecto = TimeSpan.FromHours(6);

    // Campos (datos primarios) que pedimos a CONSULTAGRAL por entidad.
    private static readonly string[] CamposBien =
    {
        "Codigo", "Descripcion", "CodigoUnidadMedida1", "CodigoUnidadMedida2",
        "DobleUnidadMedida", "RelacionStock", "TipoRelacion", "UnidadCompras",
        "AdministraPartidas", "AdministraSeries", "Impuesto"
    };
    private static readonly string[] CamposProveedor = { "Codigo", "RazonSocial" };

    public BasCacheRefresher(
        BasDestinosService destinos, BasCacheMaestros cache,
        ILogger<BasCacheRefresher> log, IHostEnvironment env)
    {
        _destinos = destinos;
        _cache = cache;
        _log = log;
        _carpeta = Path.Combine(env.ContentRootPath, "cache-padron");
    }

    // Lee del disco (instantáneo) lo que haya guardado de corridas anteriores.
    public void CargarDesdeDisco()
    {
        foreach (var b in _destinos.Nombres)
        {
            var ab = LeerArchivo<BienInfo>(b, "bienes");
            if (ab is not null)
            {
                GuardarBienes(b, ab.Datos, ab.Actualizado);
                _log.LogInformation("Caché BAS '{Base}': {N} bienes (leídos de disco).", b, ab.Datos.Count);
            }
            var ap = LeerArchivo<string>(b, "proveedores");
            if (ap is not null)
            {
                GuardarProveedores(b, ap.Datos, ap.Actualizado);
                _log.LogInformation("Caché BAS '{Base}': {N} proveedores (leídos de disco).", b, ap.Datos.Count);
            }
        }
    }

    public async Task RefrescarTodoAsync(
        CancellationToken ct = default, bool soloVencidos = false, TimeSpan? edadMaxima = null)
    {
        foreach (var b in _destinos.Nombres)
            await RefrescarBaseAsync(b, ct, soloVencidos, edadMaxima);
    }

    public async Task RefrescarBaseAsync(
        string baseNombre, CancellationToken ct = default, bool soloVencidos = false, TimeSpan? edadMaxima = null)
    {
        var edad = edadMaxima ?? EdadMaximaPorDefecto;
        await CargarBienesAsync(baseNombre, ct, soloVencidos, edad);
        await CargarProveedoresAsync(baseNombre, ct, soloVencidos, edad);
    }

    private async Task CargarBienesAsync(string b, CancellationToken ct, bool soloVencidos, TimeSpan edad)
    {
        if (soloVencidos && Vigente(b, "bienes", edad))
        {
            _log.LogInformation("Caché BAS '{Base}' bienes: vigente (de disco), no se recarga.", b);
            return;
        }
        try
        {
            var datos = await CargarConsultaGralAsync<BienInfo>(b, "/api/CONSULTAGRAL/Bien", CamposBien, ct, el =>
            {
                var cod = BienInfo.Prop(el, "Codigo");
                return string.IsNullOrEmpty(cod) ? ((string, BienInfo)?)null : (cod, BienInfo.Desde(el));
            });
            // Resolvemos la tasa de IVA de cada bien desde la tabla de impuestos
            // (Bien.Impuesto -> Impuestos.TasaIvaCompras), para autocompletarla en la
            // factura. Si la tabla no carga, los bienes quedan con tasa 0 (se tipea).
            var tasas = await CargarTasasImpuestoAsync(b, ct);
            if (tasas.Count > 0)
                foreach (var bien in datos.Values)
                    if (!string.IsNullOrEmpty(bien.Impuesto) && tasas.TryGetValue(bien.Impuesto, out var t))
                        bien.TasaIvaCompras = t;
            var ahora = DateTimeOffset.Now;
            GuardarBienes(b, datos, ahora);
            EscribirArchivo(b, "bienes", datos, ahora);
            _log.LogInformation("Caché BAS '{Base}': {N} bienes (actualizado desde BAS).", b, datos.Count);
        }
        catch (Exception ex) { MarcarError(b, "bienes", ex); }
    }

    private async Task CargarProveedoresAsync(string b, CancellationToken ct, bool soloVencidos, TimeSpan edad)
    {
        if (soloVencidos && Vigente(b, "proveedores", edad))
        {
            _log.LogInformation("Caché BAS '{Base}' proveedores: vigente (de disco), no se recarga.", b);
            return;
        }
        try
        {
            var datos = await CargarConsultaGralAsync<string>(b, "/api/CONSULTAGRAL/Proveedor", CamposProveedor, ct, el =>
            {
                var cod = BienInfo.Prop(el, "Codigo");
                return string.IsNullOrEmpty(cod) ? ((string, string)?)null : (cod, BienInfo.Prop(el, "RazonSocial") ?? "");
            });
            var ahora = DateTimeOffset.Now;
            GuardarProveedores(b, datos, ahora);
            EscribirArchivo(b, "proveedores", datos, ahora);
            _log.LogInformation("Caché BAS '{Base}': {N} proveedores (actualizado desde BAS).", b, datos.Count);
        }
        catch (Exception ex) { MarcarError(b, "proveedores", ex); }
    }

    // Carga la tabla de impuestos de la base (GET /api/Impuestos/{empresa}) y
    // devuelve un mapa código -> TasaIvaCompras. Tolerante a fallos: si algo sale
    // mal devuelve vacío (los bienes quedan sin tasa y se tipea a mano).
    private async Task<Dictionary<string, decimal>> CargarTasasImpuestoAsync(string b, CancellationToken ct)
    {
        var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cfg = _destinos.Config(b) ?? new DestinoBas();
            var json = await _destinos.GetAsync(b, "/api/Impuestos/" + cfg.Empresa, ct);
            if (string.IsNullOrWhiteSpace(json)) return dict;

            using var doc = JsonDocument.Parse(json);
            var arr = ArrayDeImpuestos(doc.RootElement);
            if (arr is null) return dict;

            foreach (var el in arr.Value.EnumerateArray())
            {
                var cod = BienInfo.Prop(el, "Codigo");
                if (string.IsNullOrEmpty(cod)) continue;
                dict[cod] = BienInfo.PropDecimal(el, "TasaIvaCompras");
            }
            _log.LogInformation("Caché BAS '{Base}': {N} impuestos (tasas de IVA de compras).", b, dict.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Caché BAS '{Base}': no se pudo cargar la tabla de impuestos. {Msg}", b, ex.Message);
        }
        return dict;
    }

    // La respuesta de /api/Impuestos suele ser un array; si viniera envuelta en un
    // objeto, devolvemos el primer array que encontremos dentro.
    private static JsonElement? ArrayDeImpuestos(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array)
                    return p.Value;
        return null;
    }

    private bool Vigente(string b, string maestro, TimeSpan edad)
    {
        var cuando = maestro == "bienes"
            ? LeerArchivo<BienInfo>(b, maestro)?.Actualizado
            : LeerArchivo<string>(b, maestro)?.Actualizado;
        return cuando.HasValue && DateTimeOffset.Now - cuando.Value < edad;
    }

    private void GuardarBienes(string b, IReadOnlyDictionary<string, BienInfo> datos, DateTimeOffset cuando)
    {
        // Reconstruimos el diccionario con comparador CASE-INSENSITIVE. Si los datos
        // vinieron de disco (deserializados de JSON) traen el comparador por defecto
        // (sensible a mayúsculas) y la resolución no encontraría "02023p" vs "02023P".
        // De paso dejamos el código canónico (la clave) dentro de cada BienInfo.
        var dict = new Dictionary<string, BienInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in datos)
        {
            if (string.IsNullOrEmpty(kv.Value.Codigo)) kv.Value.Codigo = kv.Key;
            dict[kv.Key] = kv.Value;
        }

        _cache.Actualizar(b, s => new SnapshotMaestro
        {
            Bienes = dict,
            Proveedores = s.Proveedores,
            BienesListo = true,
            ProveedoresListo = s.ProveedoresListo,
            Actualizado = cuando,
            Error = s.Error
        });
    }

    private void GuardarProveedores(string b, IReadOnlyDictionary<string, string> datos, DateTimeOffset cuando)
    {
        // Mismo motivo que en GuardarBienes: comparador case-insensitive aunque
        // los datos vengan deserializados de disco.
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in datos) dict[kv.Key] = kv.Value;

        _cache.Actualizar(b, s => new SnapshotMaestro
        {
            Bienes = s.Bienes,
            Proveedores = dict,
            BienesListo = s.BienesListo,
            ProveedoresListo = true,
            Actualizado = cuando,
            Error = s.Error
        });
    }

    private void MarcarError(string b, string maestro, Exception ex)
    {
        _cache.Actualizar(b, s => new SnapshotMaestro
        {
            Bienes = s.Bienes,
            Proveedores = s.Proveedores,
            BienesListo = s.BienesListo,
            ProveedoresListo = s.ProveedoresListo,
            Actualizado = s.Actualizado,
            Error = JuntarError(s.Error, $"{maestro}: {ex.Message}")
        });
        _log.LogWarning("Caché BAS '{Base}': no se pudo cargar {Maestro}. {Msg}", b, maestro, ex.Message);
    }

    // ---- Disco (genérico) ----
    private sealed class ArchivoMaestro<T>
    {
        public DateTimeOffset Actualizado { get; set; }
        public Dictionary<string, T> Datos { get; set; } = new();
    }

    private string RutaArchivo(string b, string maestro) => Path.Combine(_carpeta, $"{b}-{maestro}.json");

    private ArchivoMaestro<T>? LeerArchivo<T>(string b, string maestro)
    {
        try
        {
            var ruta = RutaArchivo(b, maestro);
            if (!File.Exists(ruta)) return null;
            return JsonSerializer.Deserialize<ArchivoMaestro<T>>(File.ReadAllText(ruta), JsonOpts);
        }
        catch { return null; }   // formato viejo/ilegible -> se vuelve a traer de BAS
    }

    private void EscribirArchivo<T>(string b, string maestro, IReadOnlyDictionary<string, T> datos, DateTimeOffset cuando)
    {
        try
        {
            Directory.CreateDirectory(_carpeta);
            var modelo = new ArchivoMaestro<T> { Actualizado = cuando, Datos = new Dictionary<string, T>(datos) };
            File.WriteAllText(RutaArchivo(b, maestro), JsonSerializer.Serialize(modelo, JsonOpts));
        }
        catch (Exception ex)
        {
            _log.LogWarning("No se pudo escribir la caché en disco de '{Base}' {Maestro}. {Msg}", b, maestro, ex.Message);
        }
    }

    // ---- Paginado vía CONSULTAGRAL ----
    // BAS no informa el total en headers, así que paginamos hasta que una página
    // venga vacía o más corta que las anteriores. El tamaño "efectivo" de página
    // se toma de la primera página (por si BAS topea el pageSize por debajo del
    // pedido). Se guarda contra repetición de página (si BAS clampa el número).
    private async Task<Dictionary<string, T>> CargarConsultaGralAsync<T>(
        string b, string entidadPath, string[] campos, CancellationToken ct, Func<JsonElement, (string, T)?> proyectar)
    {
        var dict = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var cfg = _destinos.Config(b) ?? new DestinoBas();

        int efectivo = -1;
        string? primerCodPrevio = null;

        for (int page = 1; page <= MaxPaginas; page++)
        {
            var cuerpo = ConstruirConsulta(cfg, campos, page);
            var json = await _destinos.PostAsync(b, entidadPath, cuerpo, ct);
            if (string.IsNullOrWhiteSpace(json)) break;

            using var doc = JsonDocument.Parse(json);
            var arr = ObtenerArrayCuerpo(doc.RootElement);
            if (arr is null) break;

            int count = 0;
            string? primerCod = null;
            foreach (var el in arr.Value.EnumerateArray())
            {
                count++;
                var r = proyectar(el);
                if (r.HasValue)
                {
                    primerCod ??= r.Value.Item1;
                    dict[r.Value.Item1] = r.Value.Item2;
                }
            }

            if (count == 0) break;                                  // página vacía -> fin
            if (primerCod != null && primerCod == primerCodPrevio) break;  // BAS repitió la página -> fin
            primerCodPrevio = primerCod;

            if (efectivo < 0) efectivo = count;                     // tamaño real de página
            else if (count < efectivo) break;                       // página corta -> última
        }

        return dict;
    }

    // Arma el cuerpo JSON de la consulta CONSULTAGRAL para una página.
    private static string ConstruirConsulta(DestinoBas cfg, string[] campos, int page)
    {
        var body = new
        {
            HEADER = new
            {
                ETIQUETA = "CONSULTAGRAL",
                CODEMP = cfg.Empresa,
                CODSUC = cfg.Sucursal,
                FECHA = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            },
            ConsultaGral = new
            {
                SelectDatosPrimarios = campos.Select(c => new { Nombre = c }).ToArray(),
                OrdenDatosPrimarios = new[] { new { Nombre = "Codigo", Orden = "A" } },
                pageSize = PageSize,
                pageNumber = page
            }
        };
        return JsonSerializer.Serialize(body);
    }

    // La respuesta de CONSULTAGRAL trae los registros en Cuerpo.{ENTIDAD} (BIENES,
    // PROVEEDORES, ...). Devuelve el primer array que haya dentro de "Cuerpo".
    private static JsonElement? ObtenerArrayCuerpo(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, "Cuerpo", StringComparison.OrdinalIgnoreCase)
                && p.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var q in p.Value.EnumerateObject())
                    if (q.Value.ValueKind == JsonValueKind.Array)
                        return q.Value;
            }
        return null;
    }

    private static string JuntarError(string? acumulado, string nuevo)
        => string.IsNullOrEmpty(acumulado) ? nuevo : acumulado + " | " + nuevo;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
