using System.Globalization;
using NPOI.HSSF.UserModel;   // .xls legacy (BIFF)
using NPOI.SS.UserModel;

namespace PortalClientes.Bas;

// Conciliación bancaria de Banco Provincia: el banco NO tiene API, se IMPORTA el .xls que se
// baja del homebanking ("Consulta de movimientos" -> Exportar a Excel). Parsea ese .xls a la
// MISMA estructura (BancoBieCuentasService.Movimiento) que Credicoop/ICBC, así el modal, el TXT
// para BAS y todo el flujo posterior se reutilizan igual.
//
// Formato del .xls (verificado, hoja "report", BIFF/OLE2 -> HSSFWorkbook):
//   fila con "Cuenta: 4013-12414/8"  -> de acá salen los dígitos de la cuenta.
//   fila de encabezado: "Fecha | Descripción | Importe | Saldo".
//   filas de datos, del MÁS NUEVO al más viejo:
//       Fecha "31-ago-2026" (mes en español abreviado) | Descripción | Importe (con signo) | Saldo
//   2 filas finales de disclaimer ("Esta información es la que consta…") -> se descartan.
// Signo: el importe ya viene CON signo (negativo = débito). => Importe = |importe|; DB si <0.
// Provincia NO trae Nº de comprobante ni código operativo separados -> quedan vacíos (como el
// ICBC cuando no hay cheque); BAS concilia por fecha + importe + descripción.
public class BancoProvinciaConciliacionService
{
    public sealed record Resultado(string Cuenta, IReadOnlyList<BancoBieCuentasService.Movimiento> Movimientos);

    private static readonly Dictionary<string, int> Meses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ene"] = 1, ["feb"] = 2, ["mar"] = 3, ["abr"] = 4, ["may"] = 5, ["jun"] = 6,
        ["jul"] = 7, ["ago"] = 8, ["sep"] = 9, ["set"] = 9, ["oct"] = 10, ["nov"] = 11, ["dic"] = 12,
    };

    public Resultado Parsear(Stream xls)
    {
        var wb = new HSSFWorkbook(xls);
        var hoja = wb.GetSheetAt(0);
        if (hoja is null) throw new InvalidOperationException("El archivo no tiene ninguna hoja.");

        var cuenta = "";
        int colFecha = -1, colDesc = -1, colImporte = -1, colSaldo = -1;
        int filaDesde = -1;

        // 1ra pasada: ubicar la cuenta y la fila de ENCABEZADO (la que tiene "Fecha" e "Importe"
        // juntas; así no confunde con la fila de metadatos "Fecha: 09/09/2026  Hora: 10:05").
        for (int r = hoja.FirstRowNum; r <= hoja.LastRowNum && filaDesde < 0; r++)
        {
            var fila = hoja.GetRow(r);
            if (fila is null) continue;
            int cF = -1, cD = -1, cI = -1, cS = -1;
            for (int c = fila.FirstCellNum; c < fila.LastCellNum; c++)
            {
                var t = Texto(fila.GetCell(c));
                if (t.Length == 0) continue;
                if (cuenta.Length == 0 && t.Replace(" ", "").StartsWith("Cuenta:", StringComparison.OrdinalIgnoreCase))
                    cuenta = SoloDigitos(t);
                var tn = t.Trim().ToLowerInvariant();
                if (tn == "fecha") cF = c;                 // exacto: el metadato es "Fecha: ..."
                else if (tn.StartsWith("descrip")) cD = c;
                else if (tn.StartsWith("importe")) cI = c;
                else if (tn.StartsWith("saldo")) cS = c;
            }
            if (cF >= 0 && cI >= 0)                        // ésta es la fila de encabezado
            {
                colFecha = cF; colDesc = cD; colImporte = cI; colSaldo = cS;
                filaDesde = r + 1;                         // los datos arrancan debajo
            }
        }
        if (filaDesde < 0 || colFecha < 0 || colImporte < 0)
            throw new InvalidOperationException("No se encontró el encabezado (Fecha / Importe) en el .xls de Provincia.");

        var movs = new List<BancoBieCuentasService.Movimiento>();
        for (int r = filaDesde; r <= hoja.LastRowNum; r++)
        {
            var fila = hoja.GetRow(r);
            if (fila is null) continue;
            var fecha = FechaYmd(Texto(fila.GetCell(colFecha)));
            if (fecha.Length == 0) continue;                    // encabezado, disclaimer o fila vacía
            var desc = colDesc >= 0 ? Texto(fila.GetCell(colDesc)) : "";
            var signo = Num(fila.GetCell(colImporte));
            if (signo == 0m && desc.Length == 0) continue;
            var ind = signo < 0 ? "DB" : "CR";
            var monto = Math.Abs(signo);
            var saldo = colSaldo >= 0 ? Num(fila.GetCell(colSaldo)) : 0m;
            movs.Add(new BancoBieCuentasService.Movimiento(fecha, desc, ind, monto, "", "", saldo, ""));
        }

        // El .xls viene del más NUEVO al más viejo -> lo dejamos cronológico (viejo -> nuevo).
        movs.Reverse();
        return new Resultado(cuenta, movs);
    }

    // "31-ago-2026" / "31/08/2026" -> yyyymmdd. Mes en español abreviado o numérico.
    private static string FechaYmd(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return "";
        var partes = s.Split('-', '/');
        if (partes.Length != 3) return "";
        if (!int.TryParse(partes[0], out var dia)) return "";
        var mesTxt = partes[1].Trim();
        int mes;
        if (!int.TryParse(mesTxt, out mes))
        {
            var clave = mesTxt.Length >= 3 ? mesTxt[..3] : mesTxt;
            if (!Meses.TryGetValue(clave, out mes)) return "";
        }
        if (!int.TryParse(partes[2], out var anio)) return "";
        if (anio < 100) anio += 2000;
        try { return new DateTime(anio, mes, dia).ToString("yyyyMMdd", CultureInfo.InvariantCulture); }
        catch { return ""; }
    }

    // Celda numérica (importe/saldo) o texto con coma decimal.
    private static decimal Num(ICell? c)
    {
        if (c is null) return 0m;
        try
        {
            if (c.CellType == CellType.Numeric ||
                (c.CellType == CellType.Formula && c.CachedFormulaResultType == CellType.Numeric))
                return (decimal)c.NumericCellValue;
        }
        catch { /* cae al parseo de texto */ }
        var s = Texto(c);
        if (s.Length == 0) return 0m;
        s = s.Replace(".", "").Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }

    private static string Texto(ICell? c)
    {
        if (c is null) return "";
        try
        {
            return c.CellType switch
            {
                CellType.String => c.StringCellValue?.Trim() ?? "",
                CellType.Numeric => c.NumericCellValue.ToString(CultureInfo.InvariantCulture),
                CellType.Formula => c.CachedFormulaResultType == CellType.String
                    ? c.StringCellValue?.Trim() ?? ""
                    : c.NumericCellValue.ToString(CultureInfo.InvariantCulture),
                _ => (c.ToString() ?? "").Trim(),
            };
        }
        catch { return (c.ToString() ?? "").Trim(); }
    }

    private static string SoloDigitos(string s) => new string((s ?? "").Where(char.IsDigit).ToArray());
}
