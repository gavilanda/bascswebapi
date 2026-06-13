using PortalClientes.Models;

namespace PortalClientes.Remitos;

// ---- Renglones ----
// Los campos PrecioUnitario y TasaIva sólo se usan en facturas (en remitos van en 0).
public record LineaRequest(
    string ProductoCodigo,
    string? Descripcion,
    decimal Cantidad,
    string? Unidad,
    string? Observacion,
    string? Partida,
    string? Series,
    decimal PrecioUnitario = 0,
    decimal TasaIva = 0,
    DateTime? FechaVencimiento = null);

public record LineaDto(
    int Id,
    string ProductoCodigo,
    string? Descripcion,
    decimal Cantidad,
    string? Unidad,
    string? Observacion,
    string? Partida,
    string? Series,
    decimal PrecioUnitario,
    decimal TasaIva,
    DateTime? FechaVencimiento,
    int Orden);

// ---- Percepciones de IIBB (sólo factura) ----
public record PercepcionIngBrRequest(
    string? Provincia,
    decimal BaseImponible,
    decimal Importe,
    string? Regimen = null);

public record PercepcionIngBrDto(
    int Id,
    string? Provincia,
    decimal BaseImponible,
    decimal Importe,
    decimal Porcentaje,
    string? Regimen);

// ---- Alta / edición ----
// Campos de factura (Letra, CondicionCompra, PercepcionIva, TotalDeclarado,
// PercepcionesIngBr) van con default para que el alta de un remito no los necesite.
public record CrearPreRemitoRequest(
    string ProveedorCodigo,
    string? ProveedorRazonSocial,
    DateTime? Fecha,
    string? TipoComprobante,          // "Remito" | "Factura" (se elige en el alta)
    string? ComprobantePrefijo,
    long? ComprobanteNumero,
    DateTime? ComprobanteFecha,
    string? Observaciones,
    string? Destino,                 // base BAS a la que se grabará (se elige en el alta)
    List<LineaRequest> Lineas,
    string? Letra = null,
    string? CondicionCompra = null,
    decimal PercepcionIva = 0,
    decimal? TotalDeclarado = null,
    List<PercepcionIngBrRequest>? PercepcionesIngBr = null);

public record ModificarPreRemitoRequest(
    string ProveedorCodigo,
    string? ProveedorRazonSocial,
    DateTime? Fecha,
    string? TipoComprobante,          // "Remito" | "Factura" (editable en Borrador)
    string? ComprobantePrefijo,
    long? ComprobanteNumero,
    DateTime? ComprobanteFecha,
    string? Observaciones,
    string? Destino,                 // base BAS a la que se grabará (editable en Borrador)
    List<LineaRequest> Lineas,
    Guid RowVersion,                 // token que tenía el cliente al cargar (concurrencia)
    string? Letra = null,
    string? CondicionCompra = null,
    decimal PercepcionIva = 0,
    decimal? TotalDeclarado = null,
    List<PercepcionIngBrRequest>? PercepcionesIngBr = null);

// Acciones que sólo bloquean/cambian estado: mandan el token para no pisar cambios.
public record AccionRemitoRequest(Guid RowVersion);

public record GrabarRequest(string Base, Guid RowVersion);

// ---- Lectura ----
public record PreRemitoListItemDto(
    int Id,
    DateTime Fecha,
    string TipoComprobante,
    string? Letra,
    string ProveedorCodigo,
    string? ProveedorRazonSocial,
    string? ComprobantePrefijo,
    long? ComprobanteNumero,
    DateTime? ComprobanteFecha,
    string Estado,
    int CantidadRenglones,
    string? DestinoBase,
    string CreadoPor,
    DateTime CreadoEn,
    DateTime? ModificadoEn);

public record PreRemitoDto(
    int Id,
    DateTime Fecha,
    string TipoComprobante,
    string ProveedorCodigo,
    string? ProveedorRazonSocial,
    string? ComprobantePrefijo,
    long? ComprobanteNumero,
    DateTime? ComprobanteFecha,
    string? Observaciones,
    string Estado,
    string? DestinoBase,
    string? BasReferencia,
    string? MensajeError,
    // ---- Factura ----
    string? Letra,
    string? CondicionCompra,
    decimal PercepcionIva,
    decimal? TotalDeclarado,
    List<PercepcionIngBrDto> PercepcionesIngBr,
    // ----
    string CreadoPor,
    DateTime CreadoEn,
    string? ModificadoPor,
    DateTime? ModificadoEn,
    string? ConformadoPor,
    DateTime? ConformadoEn,
    string? EnviadoPor,
    DateTime? EnviadoEn,
    Guid RowVersion,
    List<LineaDto> Lineas);

// Helpers de proyección entidad -> DTO.
public static class RemitoMapeo
{
    public static PreRemitoListItemDto AItem(PreRemito p) => new(
        p.Id, p.Fecha, p.TipoComprobante.ToString(), p.Letra,
        p.ProveedorCodigo, p.ProveedorRazonSocial,
        p.ComprobantePrefijo, p.ComprobanteNumero, p.ComprobanteFecha,
        p.Estado.ToString(), p.Lineas.Count, p.DestinoBase,
        p.CreadoPor, p.CreadoEn, p.ModificadoEn);

    public static PreRemitoDto ADto(PreRemito p) => new(
        p.Id, p.Fecha, p.TipoComprobante.ToString(), p.ProveedorCodigo, p.ProveedorRazonSocial,
        p.ComprobantePrefijo, p.ComprobanteNumero, p.ComprobanteFecha,
        p.Observaciones,
        p.Estado.ToString(), p.DestinoBase, p.BasReferencia, p.MensajeError,
        p.Letra, p.CondicionCompra, p.PercepcionIva, p.TotalDeclarado,
        p.PercepcionesIngBr.Select(x => new PercepcionIngBrDto(
            x.Id, x.Provincia, x.BaseImponible, x.Importe, x.Porcentaje, x.Regimen)).ToList(),
        p.CreadoPor, p.CreadoEn, p.ModificadoPor, p.ModificadoEn,
        p.ConformadoPor, p.ConformadoEn, p.EnviadoPor, p.EnviadoEn,
        p.RowVersion,
        p.Lineas.OrderBy(l => l.Orden).Select(l => new LineaDto(
            l.Id, l.ProductoCodigo, l.Descripcion, l.Cantidad, l.Unidad, l.Observacion,
            l.Partida, l.Series, l.PrecioUnitario, l.TasaIva, l.FechaVencimiento, l.Orden)).ToList());
}
