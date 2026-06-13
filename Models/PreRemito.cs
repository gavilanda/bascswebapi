namespace PortalClientes.Models;

// Estado de un ingreso de compra / entrada de mercadería.
//  Borrador   -> se está armando/editando (varios usuarios pueden tocarlo).
//  Conformado -> revisado y bloqueado, listo para grabar en BAS.
//  Enviado    -> grabado exitosamente en una base BAS.
public enum EstadoPreRemito
{
    Borrador,
    Conformado,
    Enviado
}

// Tipo de comprobante con el que se ingresa la mercadería. Define a qué API de
// BAS se manda al grabar: Remito -> /api/RemitosIngreso; Factura -> /api/ComprobantesCompra.
public enum TipoComprobante
{
    Remito,
    Factura
}

// Cabecera del ingreso. Vive en NUESTRA base hasta que se graba en BAS.
public class PreRemito
{
    public int Id { get; set; }

    // Tipo de comprobante: Remito o Factura. Se elige en el alta y determina la
    // API de BAS usada al grabar.
    public TipoComprobante TipoComprobante { get; set; } = TipoComprobante.Remito;

    // Proveedor por código BAS (más la razón social, guardada al elegirlo).
    public string ProveedorCodigo { get; set; } = "";
    public string? ProveedorRazonSocial { get; set; }

    // Fecha del comprobante propio (BAS "Fecha"). Editable: no es necesariamente hoy.
    public DateTime Fecha { get; set; } = DateTime.Today;

    // Comprobante del proveedor (datos externos -> BAS PrefijoExterno / NumeroExterno / FechaExterna).
    public string? ComprobantePrefijo { get; set; }
    public long? ComprobanteNumero { get; set; }
    public DateTime? ComprobanteFecha { get; set; }

    public string? Observaciones { get; set; }

    public EstadoPreRemito Estado { get; set; } = EstadoPreRemito.Borrador;

    // Base BAS donde se grabó (se define recién al grabar). "BARK" / "PRUEBAB".
    public string? DestinoBase { get; set; }
    // Referencia que devuelve BAS al crear el comprobante (prefijo/número e IdTransaccion).
    public string? BasReferencia { get; set; }
    // Detalle del último error de grabado, si lo hubo.
    public string? MensajeError { get; set; }

    // ======================= Campos propios de FACTURA =======================
    // (Solo se usan cuando TipoComprobante == Factura. En un remito quedan en
    //  null/0 y no se envían.)

    // Letra del comprobante: "A" / "B" / "C". Define el código de comprobante en
    // BAS (A->MA, B->MB, C->MC) y cómo se cargan los precios (ver más abajo).
    public string? Letra { get; set; }

    // Código de la condición de compra. BAS la usa para calcular el/los
    // vencimientos de la cuenta corriente (referencia a su tabla de condiciones).
    public string? CondicionCompra { get; set; }

    // Percepción de IVA del comprobante (un total de cabecera). 0 si no tiene.
    public decimal PercepcionIva { get; set; }

    // Total impreso de la factura, cargado por el usuario, para cruzarlo contra el
    // total calculado (gravado + IVA + percepciones) y avisar si no coinciden.
    public decimal? TotalDeclarado { get; set; }

    // Percepciones de Ingresos Brutos, discriminadas por provincia.
    public List<PreRemitoPercepcionIngBr> PercepcionesIngBr { get; set; } = new();

    // =========================================================================

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

// Renglón del ingreso: producto por código BAS + cantidad. Los campos de precio
// e IVA solo se usan en facturas.
public class PreRemitoLinea
{
    public int Id { get; set; }
    public int PreRemitoId { get; set; }

    public string ProductoCodigo { get; set; } = "";
    public string? Descripcion { get; set; }     // guardada al elegir, para mostrar
    public decimal Cantidad { get; set; }
    public string? Unidad { get; set; }
    public string? Observacion { get; set; }

    // Lote/partida (cuando el artículo administra partidas). Se genera automáticamente
    // al grabar ({proveedor}-{ddMMyy de la fecha del ingreso}); no se tipea a mano.
    public string? Partida { get; set; }
    // Vencimiento de la partida (lo carga el usuario por renglón). Se usa al dar de
    // alta la partida en BAS para los artículos que administran partidas.
    public DateTime? FechaVencimiento { get; set; }
    // Números de serie (cuando el artículo administra series). Texto libre por
    // ahora; cuando definamos el cálculo se convierte en ExplosionSeries de BAS.
    public string? Series { get; set; }

    // ---- Solo factura ----
    // Precio unitario cargado. Su interpretación depende de la letra:
    //   A -> neto (sin IVA); B/C -> precio final (con IVA, salvo C que no tiene IVA).
    // El gravado y el IVA por renglón se derivan de este precio + la cantidad + la tasa.
    public decimal PrecioUnitario { get; set; }
    // Alícuota de IVA del renglón (21, 10.5, 27, 0...). En facturas C va en 0.
    public decimal TasaIva { get; set; }

    public int Orden { get; set; }               // para mantener el orden de carga

    public PreRemito? PreRemito { get; set; }
}

// Percepción de Ingresos Brutos de una factura, por provincia/jurisdicción.
// El usuario carga el importe de percepción (y la base sujeta); el porcentaje se
// calcula (importe / base * 100) para armar el CoparticipacionesIngresosBrutos de BAS.
public class PreRemitoPercepcionIngBr
{
    public int Id { get; set; }
    public int PreRemitoId { get; set; }

    // Código de provincia/jurisdicción (BAS).
    public string? Provincia { get; set; }
    // Base imponible sobre la que se calcula la percepción (ImporteSujeto en BAS).
    public decimal BaseImponible { get; set; }
    // Importe de la percepción (ImportePercepcion en BAS), cargado por el usuario.
    public decimal Importe { get; set; }
    // Porcentaje calculado = Importe / BaseImponible * 100 (PorcentajePercepcion en BAS).
    public decimal Porcentaje { get; set; }
    // Régimen (opcional).
    public string? Regimen { get; set; }

    public PreRemito? PreRemito { get; set; }
}
