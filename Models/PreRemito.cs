namespace PortalClientes.Models;

// Estado de un pre-remito de compra / entrada de mercadería.
//  Borrador   -> se está armando/editando (varios usuarios pueden tocarlo).
//  Conformado -> revisado y bloqueado, listo para grabar en BAS.
//  Enviado    -> grabado exitosamente en una base BAS.
public enum EstadoPreRemito
{
    Borrador,
    Conformado,
    Enviado
}

// Cabecera del pre-remito. Vive en NUESTRA base hasta que se graba en BAS.
public class PreRemito
{
    public int Id { get; set; }

    // Proveedor por código BAS (más la razón social, guardada al elegirlo).
    public string ProveedorCodigo { get; set; } = "";
    public string? ProveedorRazonSocial { get; set; }

    // Fecha del remito propio (BAS "Fecha"). Editable: no es necesariamente hoy.
    public DateTime Fecha { get; set; } = DateTime.Today;

    // Comprobante del proveedor (datos externos -> BAS PrefijoExterno / NumeroExterno / FechaExterna).
    public string? ComprobantePrefijo { get; set; }
    public long? ComprobanteNumero { get; set; }
    public DateTime? ComprobanteFecha { get; set; }

    public string? Observaciones { get; set; }

    public EstadoPreRemito Estado { get; set; } = EstadoPreRemito.Borrador;

    // Base BAS donde se grabó (se define recién al grabar). "BARK" / "PRUEBAB".
    public string? DestinoBase { get; set; }
    // Referencia que devuelve BAS al crear el remito (prefijo/número e IdTransaccion).
    public string? BasReferencia { get; set; }
    // Detalle del último error de grabado, si lo hubo.
    public string? MensajeError { get; set; }

    // Auditoría.
    public string CreadoPor { get; set; } = "";
    public DateTime CreadoEn { get; set; } = DateTime.Now;
    public string? ModificadoPor { get; set; }
    public DateTime? ModificadoEn { get; set; }
    public string? ConformadoPor { get; set; }
    public DateTime? ConformadoEn { get; set; }
    public string? EnviadoPor { get; set; }
    public DateTime? EnviadoEn { get; set; }

    // Token de concurrencia optimista: cambia en cada modificación.
    public Guid RowVersion { get; set; } = Guid.NewGuid();

    public List<PreRemitoLinea> Lineas { get; set; } = new();
}

// Renglón del pre-remito: producto por código BAS + cantidad.
public class PreRemitoLinea
{
    public int Id { get; set; }
    public int PreRemitoId { get; set; }

    public string ProductoCodigo { get; set; } = "";
    public string? Descripcion { get; set; }     // guardada al elegir, para mostrar
    public decimal Cantidad { get; set; }
    public string? Unidad { get; set; }
    public string? Observacion { get; set; }

    // Lote/partida (cuando el artículo administra partidas).
    public string? Partida { get; set; }
    // Números de serie (cuando el artículo administra series). Texto libre por
    // ahora; cuando definamos el cálculo se convierte en ExplosionSeries de BAS.
    public string? Series { get; set; }

    public int Orden { get; set; }               // para mantener el orden de carga

    public PreRemito? PreRemito { get; set; }
}
