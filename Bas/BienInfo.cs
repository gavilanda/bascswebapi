using System.Globalization;
using System.Text.Json;

namespace PortalClientes.Bas;

// Datos del artículo (Bien) que necesitamos para armar el remito de ingreso.
// Se cachean junto al código. La unidad/relación se usará para el cálculo de
// cantidades (pendiente de definir); partidas/series para decidir qué pedir en
// el renglón.
public sealed class BienInfo
{
    public string Descripcion { get; set; } = "";
    public string? UnidadMedida1 { get; set; }    // CodigoUnidadMedida1 (ej "kilos")
    public string? UnidadMedida2 { get; set; }    // CodigoUnidadMedida2
    public bool DobleUnidadMedida { get; set; }
    public decimal RelacionStock { get; set; }
    public string? TipoRelacion { get; set; }     // ej "F"
    public string? UnidadCompras { get; set; }    // "1" / "2"
    public bool AdministraPartidas { get; set; }
    public bool AdministraSeries { get; set; }

    // Construye un BienInfo a partir del JSON crudo de BAS (un elemento Bien).
    public static BienInfo Desde(JsonElement el) => new()
    {
        Descripcion = Prop(el, "Descripcion") ?? "",
        UnidadMedida1 = Prop(el, "CodigoUnidadMedida1"),
        UnidadMedida2 = Prop(el, "CodigoUnidadMedida2"),
        DobleUnidadMedida = PropBool(el, "DobleUnidadMedida"),
        RelacionStock = PropDecimal(el, "RelacionStock"),
        TipoRelacion = Prop(el, "TipoRelacion"),
        UnidadCompras = Prop(el, "UnidadCompras"),
        AdministraPartidas = PropBool(el, "AdministraPartidas"),
        AdministraSeries = PropBool(el, "AdministraSeries"),
    };

    // ---- Lectura tolerante de propiedades (case-insensitive) ----
    public static string? Prop(JsonElement el, string nombre)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, nombre, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => p.Value.ToString()
                };
        return null;
    }

    public static bool PropBool(JsonElement el, string nombre)
    {
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.True: return true;
                    case JsonValueKind.False: return false;
                    case JsonValueKind.Number: return p.Value.GetDouble() != 0;
                    case JsonValueKind.String:
                        var s = (p.Value.GetString() ?? "").Trim();
                        return s.Equals("S", StringComparison.OrdinalIgnoreCase)
                            || s.Equals("SI", StringComparison.OrdinalIgnoreCase)
                            || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                            || s == "1";
                    default: return false;
                }
            }
        return false;
    }

    public static decimal PropDecimal(JsonElement el, string nombre)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0m;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDecimal(out var d)) return d;
                if (p.Value.ValueKind == JsonValueKind.String
                    && decimal.TryParse(p.Value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
                return 0m;
            }
        return 0m;
    }
}
