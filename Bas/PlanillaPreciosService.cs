using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace PortalClientes.Bas;

// Lee la planilla madre de precios ("Precios Mostrador BARK.xlsx") para dar de
// alta listas nuevas en BAS.
//
// Cómo está armada la planilla (verificado sobre el archivo real):
//   * Tiene 16 solapas, la mayoría ocultas. La que sirve es la SÉPTIMA VISIBLE
//     ("Revisión Bas 15-08-26"): se busca por posición y no por nombre, porque
//     el nombre lleva la fecha de la revisión y cambia cada vez.
//   * Fila 1 título, fila 2 encabezados, datos desde la fila 3.
//   * Columna A = código de ítem, C = "FINAL MOST (04)" (lista 004, ya con IVA),
//     F = "MAYORISTA (29)" (lista 029, sin IVA).
//   * Está agrupada por rubro, con filas de título ("PROMOS", "PRECIOS Snacks")
//     y encabezados repetidos. Se saltean solas: el código no es numérico.
//   * Hay precios en CERO (productos que sólo se venden por una de las dos
//     listas). Un cero no es un precio: no se manda.
//   * Al final hay un bloque de otra lista (la 008) que repite códigos ya
//     listados arriba. Como sólo interesan la 004 y la 029, de cada código vale
//     la PRIMERA aparición y las repeticiones posteriores se descartan.
public class PlanillaPreciosService
{
    public const int HojaVisible = 7;      // séptima solapa visible
    public const int FilaPrimerDato = 3;   // 1 = título, 2 = encabezados
    public const int ColCodigo = 0;        // A
    public const int ColMostrador = 2;     // C
    public const int ColMayorista = 5;     // F

    public sealed record Renglon(string Codigo, string Descripcion,
                                 decimal? Mostrador, decimal? Mayorista, int Fila);

    public sealed record Lectura(string Hoja, DateOnly? FechaDelNombre,
                                 List<Renglon> Renglones, List<string> Avisos);

    /// <summary>Lee la planilla y devuelve un renglón por código, en orden.</summary>
    public Lectura Leer(Stream archivo)
    {
        using var wb = new XSSFWorkbook(archivo);
        var hoja = VisibleNro(wb, HojaVisible);
        var avisos = new List<string>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renglones = new List<Renglon>();

        for (int i = FilaPrimerDato - 1; i <= hoja.LastRowNum; i++)
        {
            var fila = hoja.GetRow(i);
            if (fila is null) continue;

            var cod = Texto(fila.GetCell(ColCodigo));
            if (cod.Length == 0) continue;
            if (cod.EndsWith(".0", StringComparison.Ordinal)) cod = cod[..^2];
            if (!cod.All(char.IsDigit)) continue;      // título de rubro o encabezado

            if (!vistos.Add(cod))
            {
                // Repetido: es el bloque de la lista 008 del final, que no nos toca.
                avisos.Add($"Fila {i + 1}: el código {cod} ya figuraba más arriba, "
                           + "se usa la primera aparición.");
                continue;
            }

            var most = Numero(fila.GetCell(ColMostrador));
            var mayo = Numero(fila.GetCell(ColMayorista));
            if (most is null && mayo is null) continue;

            renglones.Add(new Renglon(cod, Texto(fila.GetCell(ColCodigo + 1)),
                                      most, mayo, i + 1));
        }

        return new Lectura(hoja.SheetName, FechaDelNombre(hoja.SheetName), renglones, avisos);
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

    private static ISheet VisibleNro(IWorkbook wb, int n)
    {
        int vistas = 0;
        for (int i = 0; i < wb.NumberOfSheets; i++)
        {
            if (wb.IsSheetHidden(i) || wb.IsSheetVeryHidden(i)) continue;
            if (++vistas == n) return wb.GetSheetAt(i);
        }
        throw new InvalidOperationException(
            $"La planilla no tiene {n} solapas visibles (tiene {vistas}).");
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
}
