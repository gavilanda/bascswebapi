namespace PortalClientes.Models;

// Nombres de los sucesos que se registran en la auditoría de pre-remitos.
public static class EventosAuditoria
{
    public const string Alta = "Alta";
    public const string Modificacion = "Modificacion";
    public const string Eliminacion = "Eliminacion";
    public const string Conformado = "Conformado";
    public const string Reabierto = "Reabierto";
    public const string Grabado = "Grabado";
}

// Registro de auditoría: un renglón por cada suceso sobre un pre-remito.
// Es un log de solo-agregar: nunca se modifica ni se borra, y sobrevive aunque
// se elimine el pre-remito (por eso no hay relación/FK con PreRemito).
// Guarda los datos por los que después se va a poder filtrar en "Auditoría":
// fecha/hora del suceso, usuario, proveedor y fecha del comprobante.
public class AuditoriaPreRemito
{
    public int Id { get; set; }

    public int PreRemitoId { get; set; }          // a qué pre-remito refiere (puede ya no existir)
    public string Evento { get; set; } = "";       // ver EventosAuditoria
    public string Usuario { get; set; } = "";      // quién lo hizo
    public DateTime FechaHora { get; set; } = DateTime.Now;   // cuándo (fecha de ingreso del suceso)

    // Contexto para filtrar/listar sin tener que ir al pre-remito.
    public string? ProveedorCodigo { get; set; }
    public string? ProveedorRazonSocial { get; set; }
    public DateTime? ComprobanteFecha { get; set; }
    public string? ComprobantePrefijo { get; set; }
    public long? ComprobanteNumero { get; set; }
    public string? Estado { get; set; }            // estado del pre-remito tras el suceso
    public string? Detalle { get; set; }           // texto libre (p.ej. error de grabado)
}
