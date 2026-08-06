using System.ComponentModel.DataAnnotations;

namespace PortalClientes.Models;

/// <summary>
/// Preferencias sueltas del portal, en formato clave/valor.
///
/// Para cosas que el usuario elige una vez y conviene recordar, pero que no
/// justifican una tabla propia. Hoy la usa la exportación a Discovery para
/// acordarse de la última carpeta donde se guardó el archivo.
/// </summary>
public class PreferenciaPortal
{
    public int Id { get; set; }

    /// <summary>Identificador de la preferencia (ej. "discovery.carpeta").</summary>
    [MaxLength(120)]
    public string Clave { get; set; } = "";

    [MaxLength(1000)]
    public string Valor { get; set; } = "";

    public DateTime Actualizado { get; set; } = DateTime.Now;
}
