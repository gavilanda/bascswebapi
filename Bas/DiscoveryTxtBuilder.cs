using System.Globalization;
using System.Text;

namespace PortalClientes.Bas;

// Arma el TXT de precios que importa Discovery.
//
// Discovery no impone un layout: se define un "descriptor de formato" donde se
// indica en qué columnas está cada dato. Este es el layout que quedó acordado y
// el que hay que cargar en el descriptor de Discovery:
//
//   Campo                Desde  Hasta  Ancho   Contenido
//   Fecha de vigencia      1      8      8     ddmmaaaa (sin barras)
//   (relleno)              9     12      4     espacios
//   Lista que modifica    13     13      1     1 o 2
//   (relleno)             14     14      1     espacio
//   Código de ítem        15     24     10     el de BAS, pegado a la izquierda
//   (relleno)             25     26      2     espacios
//   Precio de ítem        27     38     12     ceros a la izquierda, punto decimal
//
// Ejemplo de línea:
//   03082026    1 5568        000017160.00
//
// El archivo puede traer las dos listas juntas; Discovery genera un lote por cada
// una (con numeración automática).
public static class DiscoveryTxtBuilder
{
    public const int AnchoCodigo = 10;
    public const int AnchoPrecio = 12;

    // Una línea lista para el archivo.
    public sealed record Linea(string ListaDiscovery, string Codigo, string Descripcion,
                               decimal PrecioOriginal, decimal PrecioFinal, DateOnly Vigencia,
                               decimal? PrecioAnterior);

    /// <summary>
    /// Redondeo comercial: el 0,5 va SIEMPRE para arriba.
    /// Ojo que Math.Round por defecto hace redondeo bancario (1210,50 -> 1210),
    /// por eso el AwayFromZero explícito.
    /// </summary>
    public static decimal RedondearComercial(decimal valor)
        => Math.Round(valor, 0, MidpointRounding.AwayFromZero);

    /// <summary>Arma una línea posicional. Devuelve null si el código no entra.</summary>
    public static string? Formatear(string listaDiscovery, string codigo, decimal precio,
                                    DateOnly vigencia)
    {
        if (codigo.Length > AnchoCodigo) return null;

        var precioTxt = precio.ToString("0.00", CultureInfo.InvariantCulture)
                              .PadLeft(AnchoPrecio, '0');
        if (precioTxt.Length > AnchoPrecio) return null;

        var sb = new StringBuilder(38);
        sb.Append(vigencia.ToString("ddMMyyyy", CultureInfo.InvariantCulture)); // 1-8
        sb.Append("    ");                                                      // 9-12
        sb.Append(listaDiscovery);                                              // 13
        sb.Append(' ');                                                         // 14
        sb.Append(codigo.PadRight(AnchoCodigo));                                // 15-24
        sb.Append("  ");                                                        // 25-26
        sb.Append(precioTxt);                                                   // 27-38
        return sb.ToString();
    }

    /// <summary>
    /// Contenido completo del archivo. Fin de línea CRLF y sin BOM: es un TXT
    /// plano que lee un programa viejo, no hay que meterle nada raro adelante.
    /// </summary>
    public static byte[] Armar(IEnumerable<Linea> lineas, out List<string> descartados)
    {
        var desc = new List<string>();
        var sb = new StringBuilder();
        foreach (var l in lineas)
        {
            var txt = Formatear(l.ListaDiscovery, l.Codigo, l.PrecioFinal, l.Vigencia);
            if (txt is null) { desc.Add(l.Codigo); continue; }
            sb.Append(txt).Append("\r\n");
        }
        descartados = desc;
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }
}
