using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace PortalClientes.Bas;

// Lee la planilla madre de precios ("Precios Mostrador BARK.xlsx") para dar de
// alta listas nuevas en BAS.
//
// Cómo está armada la planilla (verificado sobre el archivo real):
//   * La solapa que sirve es "Principal" (primera visible); se busca por NOMBRE.
//   * Está agrupada por rubro, con filas de título ("PROMOS", "PRECIOS Snacks",
//     "PRECIOS Feteados (008)"…). Los datos empiezan EN EL GRUPO "PROMOS": todo lo
//     de arriba se ignora. El grupo "PRECIOS Feteados (008)" se OMITE.
//   * Columna A = código de ítem (numérico; si no es numérico, es título de grupo
//     o encabezado y se saltea). Columna B = descripción.
//   * Columna G = MOSTRADOR (lista 004, precio FINAL con IVA -> va tal cual).
//   * Columna J = MAYORISTA (lista 029, precio FINAL con IVA). BAS guarda la 029
//     SIN IVA (Discovery le re-suma el 21%), así que acá le SACAMOS el IVA (/1,21).
//   * Celda de precio VACÍA o en CERO = ese precio NO cambia (no se manda).
//   * Por las dudas, si un código aparece repetido, vale la PRIMERA aparición.
public class PlanillaPreciosService
{
    public const string HojaNombre = "Principal";   // primera solapa visible (por nombre)
    public const int ColCodigo = 0;        // A
    public const int ColDescripcion = 1;   // B
    public const int ColMostrador = 6;     // G  -> lista 004 (final, con IVA; tal cual)
    public const int ColMayorista = 9;     // J  -> lista 029 (final; se le SACA el IVA)
    public const decimal Iva = 1.21m;      // 21% — mismo criterio con que Discovery lo re-suma

    public sealed record Renglon(string Codigo, string Descripcion,
                                 decimal? Mostrador, decimal? Mayorista, int Fila);

    public sealed record Lectura(string Hoja, DateOnly? FechaDelNombre,
                                 List<Renglon> Renglones, List<string> Avisos);

    /// <summary>Lee la planilla y devuelve un renglón por código, en orden.</summary>
    public Lectura Leer(Stream archivo)
    {
        using var wb = new XSSFWorkbook(archivo);
        var hoja = HojaPorNombre(wb, HojaNombre);
        var avisos = new List<string>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renglones = new List<Renglon>();

        bool empezo = false;   // los datos arrancan en el grupo "PROMOS"
        bool omitir = false;   // el grupo "PRECIOS Feteados (008)" se saltea

        for (int i = 0; i <= hoja.LastRowNum; i++)
        {
            var fila = hoja.GetRow(i);
            if (fila is null) continue;

            var cod = Texto(fila.GetCell(ColCodigo));
            if (cod.Length == 0) continue;
            if (cod.EndsWith(".0", StringComparison.Ordinal)) cod = cod[..^2];

            if (!cod.All(char.IsDigit))
            {
                // Fila de TÍTULO de grupo (o encabezado): marca dónde empieza/termina cada bloque.
                var t = cod.ToUpperInvariant();
                if (t.Contains("PROMOS")) { empezo = true; omitir = false; }
                // OJO: hay DOS grupos con "Feteados"; sólo se omite el de la lista 008
                // ("PRECIOS Feteados (008)"). El otro grupo de feteados se procesa normal.
                else if (t.Contains("FETEAD") && t.Contains("008")) omitir = true;
                else omitir = false;                            // otro rubro: se vuelve a procesar
                continue;
            }

            if (!empezo || omitir) continue;   // antes de "PROMOS", o dentro de "Feteados"

            if (!vistos.Add(cod))
            {
                avisos.Add($"Fila {i + 1}: el código {cod} ya figuraba más arriba, "
                           + "se usa la primera aparición.");
                continue;
            }

            // MOSTRADOR (G) -> lista 004, final con IVA, tal cual. Redondeo a 2 (como guarda BAS).
            var most = ConValor(fila.GetCell(ColMostrador));
            if (most is not null) most = Math.Round(most.Value, 2, MidpointRounding.AwayFromZero);

            // MAYORISTA (J) -> viene final (con IVA); la 029 se guarda SIN IVA -> le sacamos el 21%.
            var mayoFinal = ConValor(fila.GetCell(ColMayorista));
            var mayo = mayoFinal is null
                ? (decimal?)null
                : Math.Round(mayoFinal.Value / Iva, 2, MidpointRounding.AwayFromZero);

            if (most is null && mayo is null) continue;   // sin ningún precio a cambiar

            renglones.Add(new Renglon(cod, Texto(fila.GetCell(ColDescripcion)),
                                      most, mayo, i + 1));
        }

        // La vigencia sugerida ya no sale del nombre de la solapa ("Principal" no la trae):
        // el controller propone la fecha del día.
        return new Lectura(hoja.SheetName, null, renglones, avisos);
    }

    /// <summary>
    /// La fecha que trae el nombre de la solapa ("Revisión Bas 15-08-26" -> 15/08/2026).
    /// Sirve para proponer la vigencia; el usuario la puede cambiar.
    /// </summary>
    public static DateOnly? FechaDelNombre(string nombre)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            nombre ?? "", @"(\d{1,2})[-/](\d{1,2})[-/](\d{2,4})");
        if (!m.Success) return null;
        int d = int.Parse(m.Groups[1].Value), mes = int.Parse(m.Groups[2].Value);
        int a = int.Parse(m.Groups[3].Value);
        if (a < 100) a += 2000;
        try { return new DateOnly(a, mes, d); } catch { return null; }
    }

    // Busca la solapa por NOMBRE (case-insensitive) entre las VISIBLES. Si no está por
    // nombre, cae en la primera visible (la "Principal" es la primera visible).
    private static ISheet HojaPorNombre(IWorkbook wb, string nombre)
    {
        ISheet? primeraVisible = null;
        for (int i = 0; i < wb.NumberOfSheets; i++)
        {
            if (wb.IsSheetHidden(i) || wb.IsSheetVeryHidden(i)) continue;
            primeraVisible ??= wb.GetSheetAt(i);
            if (string.Equals((wb.GetSheetName(i) ?? "").Trim(), nombre, StringComparison.OrdinalIgnoreCase))
                return wb.GetSheetAt(i);
        }
        return primeraVisible
            ?? throw new InvalidOperationException("La planilla no tiene ninguna solapa visible.");
    }

    private static string Texto(ICell? c)
    {
        if (c is null) return "";
        return c.CellType switch
        {
            CellType.String => c.StringCellValue?.Trim() ?? "",
            CellType.Numeric => c.NumericCellValue.ToString("0.#####"),
            CellType.Formula => c.CachedFormulaResultType == CellType.String
                ? c.StringCellValue?.Trim() ?? ""
                : c.NumericCellValue.ToString("0.#####"),
            _ => (c.ToString() ?? "").Trim(),
        };
    }

    /// <summary>El valor numérico de la celda, o null si no lo es. Las fórmulas
    /// se leen por su valor cacheado: Excel lo deja guardado al grabar.</summary>
    private static decimal? Numero(ICell? c)
    {
        if (c is null) return null;
        try
        {
            if (c.CellType == CellType.Numeric) return (decimal)c.NumericCellValue;
            if (c.CellType == CellType.Formula
                && c.CachedFormulaResultType == CellType.Numeric)
                return (decimal)c.NumericCellValue;
        }
        catch { }
        return null;
    }

    // Valor numérico de la celda tratando VACÍO y CERO como "sin precio" (null): un cero
    // no es un precio (ese precio no cambia), igual que una celda en blanco.
    private static decimal? ConValor(ICell? c)
    {
        var v = Numero(c);
        return (v is null || v.Value == 0m) ? null : v;
    }
}
