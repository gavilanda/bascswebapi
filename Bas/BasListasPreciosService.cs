using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace PortalClientes.Bas;

// Precios de listas de BAS, para exportarlos a Discovery.
//
// Se leen con CONSULTAGRAL/SQL (BAS no expone las listas de precios como entidad
// con nombre). Trampas de ese endpoint, todas contempladas acá:
//   * El motor envuelve el SELECT con FOR XML: con varios JOIN anida las columnas
//     por tabla y se pierden datos -> hay que envolver todo en una subconsulta
//     (SELECT * FROM (...) x) para que salga plano.
//   * El parámetro Top del cuerpo se IGNORA: sin TOP n en el SQL corta en 100
//     filas EN SILENCIO -> siempre TOP explícito.
//   * Un resultado vacío no vuelve vacío: responde "Data is Null..." -> es lista
//     vacía, no un error.
//   * Nada de comentarios "--" en el SQL: el motor lo colapsa en una línea y el
//     comentario se come el resto de la consulta.
public class BasListasPreciosService
{
    private readonly BasDestinosService _destinos;

    public BasListasPreciosService(BasDestinosService destinos) => _destinos = destinos;

    // Un precio vigente de una lista. Anterior es el que regía justo antes del
    // período consultado (null si el producto no tenía precio en esa lista).
    public sealed record PrecioLista(string Lista, string Codigo, string Descripcion,
                                     decimal Precio, DateOnly Vigencia, decimal? Anterior);

    /// <summary>
    /// Precios de las listas indicadas que pasaron a regir DESDE la fecha dada.
    /// Sólo bienes (los servicios no van a Discovery) y sólo con precio distinto de cero.
    /// Si un producto tuvo varios cambios dentro del período, devuelve el más reciente.
    ///
    /// Con <paramref name="soloCambios"/> devuelve únicamente los que quedaron con
    /// un precio distinto al que regía antes. Hace falta porque una lista nueva se
    /// arma copiando la anterior y después se retocan algunos precios: sin este
    /// filtro salen los cientos de renglones copiados junto con los pocos que
    /// realmente cambiaron.
    ///
    /// La comparación es contra el último precio ANTERIOR a la fecha pedida (no
    /// contra la vigencia inmediata anterior a cada cambio): así, si hubo dos
    /// actualizaciones dentro del período, se ve el cambio neto y no se pierde lo
    /// que se movió en la primera.
    /// </summary>
    public async Task<List<PrecioLista>> PreciosDesdeAsync(
        string baseNombre, IReadOnlyList<string> listas, DateOnly desde,
        bool soloCambios = false, CancellationToken ct = default)
    {
        if (listas.Count == 0) return new List<PrecioLista>();
        var enLista = string.Join(",", listas.Select(l => "'" + l.Replace("'", "''") + "'"));
        var d = desde.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var filtro = soloCambios ? "AND (v.PRECIO IS NULL OR v.PRECIO <> n.PRECIO)" : "";

        var sql = $@"
SELECT * FROM (
  SELECT TOP 100000
         RTRIM(n.CODLIS) AS LISTA,
         RTRIM(i.CODITM) AS COD,
         RTRIM(i.DESCRIPCION) AS DESCR,
         n.PRECIO AS PRECIO,
         v.PRECIO AS ANT,
         CONVERT(char(8), n.VIGENCIA, 112) AS VIG
  FROM (SELECT lp.CODLIS, lp.CODITM, lp.PRECIO, lp.VIGENCIA
        FROM dbo.LISTASPRECIOS lp WITH (NOLOCK)
        JOIN (SELECT CODLIS, CODITM, MAX(VIGENCIA) AS MaxVig
              FROM dbo.LISTASPRECIOS WITH (NOLOCK)
              WHERE CODLIS IN ({enLista}) AND VIGENCIA >= '{d}'
              GROUP BY CODLIS, CODITM) m
          ON m.CODLIS = lp.CODLIS AND m.CODITM = lp.CODITM AND m.MaxVig = lp.VIGENCIA
        WHERE lp.CODLIS IN ({enLista}) AND lp.PRECIO <> 0) n
  JOIN dbo.ITEMS i WITH (NOLOCK) ON i.CODITM = n.CODITM
  LEFT JOIN (SELECT lp.CODLIS, lp.CODITM, lp.PRECIO
             FROM dbo.LISTASPRECIOS lp WITH (NOLOCK)
             JOIN (SELECT CODLIS, CODITM, MAX(VIGENCIA) AS MaxVig
                   FROM dbo.LISTASPRECIOS WITH (NOLOCK)
                   WHERE CODLIS IN ({enLista}) AND VIGENCIA < '{d}'
                   GROUP BY CODLIS, CODITM) m2
               ON m2.CODLIS = lp.CODLIS AND m2.CODITM = lp.CODITM AND m2.MaxVig = lp.VIGENCIA
             WHERE lp.CODLIS IN ({enLista})) v
    ON v.CODLIS = n.CODLIS AND v.CODITM = n.CODITM
  WHERE i.SUSPENDIDOS = 0
    AND i.ITEMPREFI = 'B'
    {filtro}
) x ORDER BY x.LISTA, x.COD";

        var cuerpo = await ConsultarAsync(baseNombre, sql, ct);
        var salida = new List<PrecioLista>();
        if (string.IsNullOrWhiteSpace(cuerpo)) return salida;

        foreach (var fila in LeerFilas(cuerpo))
        {
            if (!fila.TryGetValue("COD", out var cod) || cod.Length == 0) continue;
            if (!decimal.TryParse(fila.GetValueOrDefault("PRECIO"), NumberStyles.Any,
                                  CultureInfo.InvariantCulture, out var precio)) continue;
            var vig = fila.GetValueOrDefault("VIG") ?? "";
            var fecha = DateOnly.TryParseExact(vig, "yyyyMMdd", CultureInfo.InvariantCulture,
                                               DateTimeStyles.None, out var f)
                        ? f : DateOnly.FromDateTime(DateTime.Today);
            decimal? ant = decimal.TryParse(fila.GetValueOrDefault("ANT"), NumberStyles.Any,
                                            CultureInfo.InvariantCulture, out var a) ? a : null;
            salida.Add(new PrecioLista(
                fila.GetValueOrDefault("LISTA") ?? "",
                cod, fila.GetValueOrDefault("DESCR") ?? "", precio, fecha, ant));
        }
        return salida;
    }

    /// <summary>
    /// Fecha del último cambio de precios de esas listas. Sirve para arrancar la
    /// pantalla en una fecha con datos: los precios se cargan un día antes con
    /// vigencia futura, así que "hoy" casi nunca trae nada.
    /// </summary>
    public async Task<DateOnly?> UltimaVigenciaAsync(
        string baseNombre, IReadOnlyList<string> listas, CancellationToken ct = default)
    {
        if (listas.Count == 0) return null;
        var enLista = string.Join(",", listas.Select(l => "'" + l.Replace("'", "''") + "'"));

        var sql = $@"
SELECT * FROM (
  SELECT TOP 1 CONVERT(char(8), MAX(lp.VIGENCIA), 112) AS VIG
  FROM dbo.LISTASPRECIOS lp WITH (NOLOCK)
  JOIN dbo.ITEMS i WITH (NOLOCK) ON i.CODITM = lp.CODITM
  WHERE lp.CODLIS IN ({enLista})
    AND lp.PRECIO <> 0
    AND i.SUSPENDIDOS = 0
    AND i.ITEMPREFI = 'B'
) x";

        var cuerpo = await ConsultarAsync(baseNombre, sql, ct);
        if (string.IsNullOrWhiteSpace(cuerpo)) return null;
        foreach (var fila in LeerFilas(cuerpo))
            if (DateOnly.TryParseExact(fila.GetValueOrDefault("VIG") ?? "", "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)) return f;
        return null;
    }

    /// <summary>
    /// Precio VIGENTE hoy de cada código en cada lista pedida.
    /// Sin filtrar por tipo de ítem ni por suspendidos: acá se compara contra lo
    /// que trae una planilla, que puede incluir cualquier cosa.
    /// </summary>
    public async Task<Dictionary<string, Dictionary<string, decimal>>> VigentesAsync(
        string baseNombre, IReadOnlyList<string> listas, IReadOnlyList<string> codigos,
        CancellationToken ct = default)
    {
        var salida = new Dictionary<string, Dictionary<string, decimal>>();
        if (listas.Count == 0 || codigos.Count == 0) return salida;
        var enListas = string.Join(",", listas.Select(l => "'" + l.Replace("'", "''") + "'"));
        var enCodigos = string.Join(",", codigos.Select(c => "'" + c.Replace("'", "''") + "'"));

        var sql = $@"
SELECT * FROM (
  SELECT TOP 100000
         RTRIM(lp.CODLIS) AS LISTA,
         RTRIM(lp.CODITM) AS COD,
         lp.PRECIO AS PRECIO
  FROM dbo.LISTASPRECIOS lp WITH (NOLOCK)
  JOIN (SELECT CODLIS, CODITM, MAX(VIGENCIA) AS MaxVig
        FROM dbo.LISTASPRECIOS WITH (NOLOCK)
        WHERE CODLIS IN ({enListas}) AND VIGENCIA <= GETDATE()
        GROUP BY CODLIS, CODITM) m
    ON m.CODLIS = lp.CODLIS AND m.CODITM = lp.CODITM AND m.MaxVig = lp.VIGENCIA
  WHERE lp.CODLIS IN ({enListas})
    AND lp.PRECIO <> 0
    AND RTRIM(lp.CODITM) IN ({enCodigos})
) x";

        var cuerpo = await ConsultarAsync(baseNombre, sql, ct);
        if (string.IsNullOrWhiteSpace(cuerpo)) return salida;
        foreach (var fila in LeerFilas(cuerpo))
        {
            var lis = fila.GetValueOrDefault("LISTA") ?? "";
            var cod = fila.GetValueOrDefault("COD") ?? "";
            if (lis.Length == 0 || cod.Length == 0) continue;
            if (!decimal.TryParse(fila.GetValueOrDefault("PRECIO"), NumberStyles.Any,
                                  CultureInfo.InvariantCulture, out var precio)) continue;
            if (!salida.TryGetValue(lis, out var d))
                salida[lis] = d = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            d[cod] = precio;
        }
        return salida;
    }

    public sealed record ItemAlta(string CodigoItem, decimal Precio);

    /// <summary>
    /// Da de alta una vigencia nueva en una lista de precios de BAS.
    ///
    /// No hace falta mandar la lista entera: BAS resuelve el precio vigente por
    /// ítem (MAX(VIGENCIA) por CODITM), así que los productos que no van en el
    /// alta siguen tomando su vigencia anterior. Probado contra BARKTEST.
    /// </summary>
    public async Task<string> CrearListaAsync(
        string baseNombre, string codigoLista, DateOnly vigencia,
        IReadOnlyList<ItemAlta> items, string observaciones, CancellationToken ct = default)
    {
        var cfg = _destinos.Config(baseNombre)
            ?? throw new InvalidOperationException($"Destino BAS desconocido: {baseNombre}");

        var body = JsonSerializer.Serialize(new
        {
            Codigo = codigoLista,
            EmpresaAlta = cfg.Empresa,
            FechaVigencia = vigencia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Observaciones = observaciones,
            // Redondeo a 2 decimales: la planilla MUESTRA 2 decimales, pero columnas como la 029
            // ("MAYORISTA sin IVA") son FÓRMULAS (ej. mostrador/1.21) cuyo valor real guardado tiene
            // decimales infinitos (82.6446280…). Se lee el valor crudo, así que sin redondear BAS
            // responde 400 (guarda decimal(18,5)). Redondeamos a 2 = lo que se ve en el Excel.
            Items = items.Select(i => new
            {
                i.CodigoItem,
                Precio = Math.Round(i.Precio, 2, MidpointRounding.AwayFromZero),
            }).ToArray(),
        });

        return await _destinos.PostAsync(baseNombre, "/api/ListasPrecios", body, ct) ?? "";
    }

    // POST a CONSULTAGRAL/SQL. Devuelve el contenido de "Cuerpo" (XML) o "" si no hubo filas.
    private async Task<string> ConsultarAsync(string baseNombre, string sql, CancellationToken ct)
    {
        var cfg = _destinos.Config(baseNombre)
            ?? throw new InvalidOperationException($"Destino BAS desconocido: {baseNombre}");
        var body = JsonSerializer.Serialize(new
        {
            HEADER = new
            {
                ETIQUETA = "CONSULTAGRAL",
                CODEMP = cfg.Empresa,
                CODSUC = cfg.Sucursal,
                FECHA = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            },
            ConsultaGral = new { SQL = sql, Timeout = 120 }
        });

        var respuesta = await _destinos.PostAsync(baseNombre, "/api/CONSULTAGRAL/SQL", body, ct);
        if (string.IsNullOrWhiteSpace(respuesta)) return "";

        using var doc = JsonDocument.Parse(respuesta);
        if (!doc.RootElement.TryGetProperty("Cuerpo", out var cuerpoEl)) return "";
        var cuerpo = cuerpoEl.GetString() ?? "";
        if (cuerpo.Length == 0) return "";
        if (!cuerpo.Contains('<'))
        {
            // Sin filas BAS no devuelve vacío: contesta este texto.
            if (cuerpo.Contains("Data is Null", StringComparison.OrdinalIgnoreCase)) return "";
            throw new InvalidOperationException("BAS rechazó la consulta: " + cuerpo);
        }
        return cuerpo;
    }

    // El XML viene como <ConsultaGral><x><CAMPO>valor</CAMPO>...</x>...</ConsultaGral>.
    private static IEnumerable<Dictionary<string, string>> LeerFilas(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { doc = XDocument.Parse(xml.Replace("&", "&amp;")); }
        if (doc.Root is null) yield break;
        foreach (var fila in doc.Root.Elements())
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var campo in fila.Elements())
                if (!campo.HasElements) d[campo.Name.LocalName] = (campo.Value ?? "").Trim();
            if (d.Count > 0) yield return d;
        }
    }
}
