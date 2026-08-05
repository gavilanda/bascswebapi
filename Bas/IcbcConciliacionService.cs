using System.Globalization;
using System.Text;

namespace PortalClientes.Bas;

// Conciliación bancaria de ICBC: el banco NO tiene API, se IMPORTA el CSV que se baja del
// homebanking. Parsea ese CSV a la MISMA estructura (BancoBieCuentasService.Movimiento) que
// usa Credicoop, así el modal, el TXT para BAS y todo el flujo posterior se reutilizan igual.
//
// Formato del CSV (verificado): UTF-8 con BOM, separador ';'.
//   línea 0: título con la cuenta  -> "Movimientos de CC $ 0513/02104031/13"
//   línea 1: encabezado (17 columnas)
//   resto  : movimientos, del MÁS NUEVO al más viejo.
// Columnas: Fecha contable;Cod de Concepto;Concepto;Debito en $;Credito en $;Saldo en $;
//           Informacion Complementaria;Nro de cheque;Sucursal Origen;Canal;Banco;CBU/Alias;
//           Tipo trf;Referencia;Nombre;Tipo doc;Nro doc
// Signo: crédito viene POSITIVO en "Credito en $"; débito viene NEGATIVO en "Debito en $".
//   => importe con signo = Debito + Credito  (una está vacía). Importe = |signo|; DB si <0.
public class IcbcConciliacionService
{
    public sealed record Resultado(string Cuenta, IReadOnlyList<BancoBieCuentasService.Movimiento> Movimientos);

    public Resultado Parsear(Stream csv)
    {
        // utf-8-sig: dejamos que StreamReader detecte y descarte el BOM.
        using var rd = new StreamReader(csv, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var texto = rd.ReadToEnd();
        var lineas = texto.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var cuenta = "";
        var movs = new List<BancoBieCuentasService.Movimiento>();

        foreach (var linea in lineas)
        {
            var l = linea.Trim();
            if (l.Length == 0) continue;

            // Título con la cuenta (primera línea que no es dato). Extraemos los dígitos.
            if (cuenta.Length == 0 && !EsFilaDato(l))
            {
                cuenta = SoloDigitos(UltimoTokenConBarra(l));
                continue;
            }
            if (!EsFilaDato(l)) continue;   // encabezado u otra fila no-dato

            var c = l.Split(';');
            if (c.Length < 6) continue;
            var fecha = FechaYmd(c[0]);                    // dd/MM/yyyy -> yyyymmdd
            if (fecha.Length == 0) continue;
            var deb = Num(c[3]);                            // débito (negativo)
            var cre = Num(c[4]);                            // crédito (positivo)
            var signo = deb + cre;                          // una está en cero
            var ind = signo < 0 ? "DB" : "CR";
            var monto = Math.Abs(signo);
            var saldo = Num(c[5]);
            var concepto = Col(c, 2);
            var nombre = Col(c, 14);
            var desc = string.IsNullOrWhiteSpace(nombre) ? concepto : $"{concepto} - {nombre}";
            var nroComp = Col(c, 7);                        // Nro de cheque
            var codOp = Col(c, 1);                          // Cod de Concepto
            movs.Add(new BancoBieCuentasService.Movimiento(fecha, desc, ind, monto, nroComp, codOp, saldo, ""));
        }

        // El CSV viene del más NUEVO al más viejo -> lo dejamos cronológico (viejo -> nuevo).
        movs.Reverse();
        return new Resultado(cuenta, movs);
    }

    // Una fila de datos empieza con una fecha dd/MM/yyyy.
    private static bool EsFilaDato(string l)
    {
        var p = l.Split(';');
        return p.Length > 0 && DateTime.TryParseExact(p[0].Trim(), "dd/MM/yyyy",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static string FechaYmd(string ddMMyyyy)
        => DateTime.TryParseExact(ddMMyyyy.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var f)
            ? f.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "";

    // "407863,29" / "-10994,72" -> decimal. Coma decimal, sin separador de miles; por las
    // dudas si viniera con punto de miles, lo quitamos.
    private static decimal Num(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return 0m;
        s = s.Replace(".", "").Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }

    private static string Col(string[] c, int i) => i < c.Length ? c[i].Trim() : "";

    // Del título ("Movimientos de CC $ 0513/02104031/13") toma el token con barras.
    private static string UltimoTokenConBarra(string s)
    {
        var tokens = s.Split(' ', '\t');
        for (int i = tokens.Length - 1; i >= 0; i--)
            if (tokens[i].Contains('/')) return tokens[i];
        return tokens.Length > 0 ? tokens[^1] : "";
    }

    private static string SoloDigitos(string s) => new string((s ?? "").Where(char.IsDigit).ToArray());
}
