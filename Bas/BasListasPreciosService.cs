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

    // Un precio vigente de una lista.
    public sealed record PrecioLista(string Lista, string Codigo, string Descripcion,
                                     decimal Precio, DateOnly Vigencia);

    /// <summary>
    /// Precios de las listas indicadas que pasaron a regir DESDE la fecha dada.
    /// Sólo bienes (los servicios no van a Discovery) y sólo con precio distinto de cero.
    /// Si un producto tuvo varios cambios dentro del período, devuelve el más reciente.
    /// </summary>
    public async Task<List<PrecioLista>> PreciosDesdeAsync(
        string baseNombre, IReadOnlyList<string> listas, DateOnly desde,
        CancellationToken ct = default)
    {
        if (listas.Count == 0) return new List<PrecioLista>();
        var enLista = string.Join(",", listas.Select(l => "'" + l.Replace("'", "''") + "'"));
        var d = desde.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var sql = $@"
SELECT * FROM (
  SELECT TOP 100000
         RTRIM(lp.CODLIS) AS LISTA,
         RTRIM(i.CODITM) AS COD,
         RTRIM(i.DESCRIPCION) AS DESCR,
         lp.PRECIO AS PRECIO,
         CONVERT(char(8), lp.VIGENCIA, 112) AS VIG
  FROM dbo.LISTASPRECIOS lp WITH (NOLOCK)
  JOIN dbo.ITEMS i WITH (NOLOCK) ON i.CODITM = lp.CODITM
  JOIN (SELECT CODLIS, CODITM, MAX(VIGENCIA) AS MaxVig
        FROM dbo.LISTASPRECIOS WITH (NOLOCK)
        WHERE CODLIS IN ({enLista}) AND VIGENCIA >= '{d}'
        GROUP BY CODLIS, CODITM) m
    ON m.CODLIS = lp.CODLIS AND m.CODITM = lp.CODITM AND m.MaxVig = lp.VIGENCIA
  WHERE lp.CODLIS IN ({enLista})
    AND lp.PRECIO <> 0
    AND i.SUSPENDIDOS = 0
    AND i.ITEMPREFI = 'B'
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
            salida.Add(new PrecioLista(
                fila.GetValueOrDefault("LISTA") ?? "",
                cod, fila.GetValueOrDefault("DESCR") ?? "", precio, fecha));
        }
        return salida;
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
