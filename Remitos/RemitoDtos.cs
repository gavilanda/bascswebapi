using PortalClientes.Models;

namespace PortalClientes.Remitos;

// ---- Renglones ----
public record LineaRequest(
    string ProductoCodigo,
    string? Descripcion,
    decimal Cantidad,
    string? Unidad,
    string? Observacion,
    string? Partida,
    string? Series);

public record LineaDto(
    int Id,
    string ProductoCodigo,
    string? Descripcion,
    decimal Cantidad,
    string? Unidad,
    string? Observacion,
    string? Partida,
    string? Series,
    int Orden);

// ---- Alta / edición ----
public record CrearPreRemitoRequest(
    string ProveedorCodigo,
    string? ProveedorRazonSocial,
    DateTime? Fecha,
    string? ComprobantePrefijo,
    long? ComprobanteNumero,
    DateTime? ComprobanteFecha,
    string? Observaciones,
    List<LineaRequest> Lineas);

public record ModificarPreRemitoRequest(
    string ProveedorCodigo,
    string? ProveedorRazonSocial,
    DateTime? Fecha,
    string? ComprobantePrefijo,
    long? ComprobanteNumero,
    DateTime? ComprobanteFecha,
    string? Observaciones,
    List<LineaRequest> Lineas,
    Guid RowVersion);   // token que tenía el cliente al cargar (concurrencia)

// Acciones que sólo bloquean/cambian estado: mandan el token para no pisar cambios.
public record AccionRemitoRequest(Guid RowVersion);

public record GrabarRequest(string Base, Guid RowVersion);

// ---- Lectura ----
public record PreRemitoListItemDto(
    int Id,
    DateTime Fecha,
    string ProveedorCodigo,
    string? ProveedorRazonSocial,
    string? ComprobantePrefijo,
    long? ComprobanteNumero,
    string Estado,
    int CantidadRenglones,
    string? DestinoBase,
    string CreadoPor,
    DateTime CreadoEn,
    DateTime? ModificadoEn);

public record PreRemitoDto(
    int Id,
    DateTime Fecha,
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
        p.Id, p.Fecha, p.ProveedorCodigo, p.ProveedorRazonSocial,
        p.ComprobantePrefijo, p.ComprobanteNumero,
        p.Estado.ToString(), p.Lineas.Count, p.DestinoBase,
        p.CreadoPor, p.CreadoEn, p.ModificadoEn);

    public static PreRemitoDto ADto(PreRemito p) => new(
        p.Id, p.Fecha, p.ProveedorCodigo, p.ProveedorRazonSocial,
        p.ComprobantePrefijo, p.ComprobanteNumero, p.ComprobanteFecha,
        p.Observaciones,
        p.Estado.ToString(), p.DestinoBase, p.BasReferencia, p.MensajeError,
        p.CreadoPor, p.CreadoEn, p.ModificadoPor, p.ModificadoEn,
        p.ConformadoPor, p.ConformadoEn, p.EnviadoPor, p.EnviadoEn,
        p.RowVersion,
        p.Lineas.OrderBy(l => l.Orden).Select(l => new LineaDto(
            l.Id, l.ProductoCodigo, l.Descripcion, l.Cantidad, l.Unidad, l.Observacion,
            l.Partida, l.Series, l.Orden)).ToList());
}
