using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PortalClientes.Bas;
using PortalClientes.Data;

namespace PortalClientes.Controllers;

[ApiController]
[Route("api/mi-cuenta")]
[Authorize] // Requiere un token valido del portal.
public class MiCuentaController : ControllerBase
{
    private readonly BasCuentaCorrienteService _ctaCte;
    private readonly BasClientesService _clientes;
    private readonly BasComprobantesService _comprobantes;
    private readonly BasDestinosService _destinos;
    private readonly BasCacheMaestros _cache;
    private readonly PortalDbContext _db;

    public MiCuentaController(
        BasCuentaCorrienteService ctaCte,
        BasClientesService clientes,
        BasComprobantesService comprobantes,
        BasDestinosService destinos,
        BasCacheMaestros cache,
        PortalDbContext db)
    {
        _ctaCte = ctaCte;
        _clientes = clientes;
        _comprobantes = comprobantes;
        _destinos = destinos;
        _cache = cache;
        _db = db;
    }

    // Bases que se consolidan en el portal del cliente: las ACTIVAS marcadas para
    // incluir en el portal (configurable por base desde la intranet), INTERSECTADAS
    // con las bases que el usuario tiene TILDADAS (claims "basePortal").
    // El usuario ve SOLO las bases tildadas: si no tiene ninguna, no ve ninguna.
    private IReadOnlyList<string> PortalBases()
    {
        var delPortal = _destinos.Nombres
            .Where(n => _destinos.Config(n) is { Activa: true, IncluirEnPortal: true })
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var delUsuario = User.FindAll("basePortal")
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Sólo las tildadas por el usuario (sin fallback a "todas").
        return delPortal.Where(n => delUsuario.Contains(n)).ToList();
    }

    // Bases a consultar en una consulta puntual: PortalBases() intersectado con las pedidas
    // por el front (CSV en ?bases=). Sin el parámetro → todas (compatibilidad). Sirve para
    // que el usuario excluya bases (ej. una caída) y no se consulten.
    private IReadOnlyList<string> FiltrarBases(string? bases)
    {
        var todas = PortalBases();
        var pedidas = (bases ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pedidas.Length == 0) return todas;
        var set = new HashSet<string>(pedidas, StringComparer.OrdinalIgnoreCase);
        return todas.Where(b => set.Contains(b)).ToList();
    }

    // ¿El usuario es staff interno habilitado a consultar el portal? (interno + accedePortal).
    private bool EsStaff()
        => User.FindFirstValue("tipo") == "Interno"
           && User.FindFirstValue("accedePortal") == "true";

    // CUIT del cliente a consultar:
    //  - Staff: el ?cuit= elegido en la búsqueda (obligatorio; sin él, que elija).
    //  - Cliente extranet: su propio CUIT (identificador), ignorando cualquier ?cuit=.
    //  - Otro: sin acceso.
    private (string? cuit, string? error) ResolverCuitObjetivo(string? cuitParam)
    {
        if (EsStaff())
        {
            var c = (cuitParam ?? "").Trim();
            return string.IsNullOrWhiteSpace(c)
                ? (null, "Elegí un cliente para ver su cuenta.")
                : (c, null);
        }
        var esCliente = User.FindFirstValue("esCliente") == "true";
        var propio = User.FindFirstValue("identificador");
        if (esCliente && !string.IsNullOrWhiteSpace(propio))
            return (propio, null);
        return (null, "Tu usuario no tiene una cuenta corriente de cliente asociada.");
    }

    // GET /api/mi-cuenta/buscar-clientes?q=&limit=
    // Solo staff. Busca en el caché de clientes de las bases del usuario, dedup por
    // CUIT (un mismo cliente puede estar en varias bases con distinto código).
    [HttpGet("buscar-clientes")]
    public ActionResult BuscarClientes([FromQuery] string q = "", [FromQuery] int limit = 30)
    {
        if (!EsStaff())
            return StatusCode(403, new { mensaje = "Solo el personal interno puede buscar clientes." });

        var texto = (q ?? "").Trim();
        limit = Math.Clamp(limit, 1, 100);

        var porCuit = new Dictionary<string, ClienteAgrupado>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in PortalBases())
        {
            foreach (var cli in _cache.Obtener(b).Clientes.Values)
            {
                var cuit = (cli.Cuit ?? "").Trim();
                if (cuit.Length == 0) continue;
                if (!porCuit.TryGetValue(cuit, out var acc))
                {
                    acc = new ClienteAgrupado { RazonSocial = cli.RazonSocial ?? "" };
                    porCuit[cuit] = acc;
                }
                if (string.IsNullOrEmpty(acc.RazonSocial) && !string.IsNullOrEmpty(cli.RazonSocial))
                    acc.RazonSocial = cli.RazonSocial;
                acc.Bases.Add(b);
            }
        }

        IEnumerable<KeyValuePair<string, ClienteAgrupado>> filtrados = porCuit;
        if (texto.Length > 0)
            filtrados = filtrados.Where(kv =>
                kv.Key.Contains(texto, StringComparison.OrdinalIgnoreCase)
                || kv.Value.RazonSocial.Contains(texto, StringComparison.OrdinalIgnoreCase));

        var ordenados = filtrados.OrderBy(kv => kv.Value.RazonSocial, StringComparer.OrdinalIgnoreCase).ToList();
        var total = ordenados.Count;
        var items = ordenados.Take(limit).Select(kv => new
        {
            cuit = kv.Key,
            razonSocial = kv.Value.RazonSocial,
            bases = kv.Value.Bases.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        }).ToList();

        return Ok(new { total, mostrando = items.Count, hayMas = total > items.Count, items });
    }

    private sealed class ClienteAgrupado
    {
        public string RazonSocial = "";
        public HashSet<string> Bases = new(StringComparer.OrdinalIgnoreCase);
    }

    // GET /api/mi-cuenta/perfil  -> datos del token.
    [HttpGet("perfil")]
    public ActionResult Perfil()
    {
        return Ok(new
        {
            identificador = User.FindFirstValue("identificador"),
            tipo = User.FindFirstValue("tipo"),
            esCliente = User.FindFirstValue("esCliente"),
            esProveedor = User.FindFirstValue("esProveedor"),
            codigoCliente = User.FindFirstValue("codigoCliente"),
            codigoProveedor = User.FindFirstValue("codigoProveedor")
        });
    }

    // ---- Preferencia de la barra "Ver bases" (orden + destildadas), POR USUARIO y
    // COMPARTIDA entre cuenta corriente y estadísticas. El front arma/parsea el JSON
    // { orden:[...], ocultas:[...] }; acá sólo lo guardamos/leemos contra el usuario logueado.
    [HttpGet("pref-bases")]
    public async Task<ActionResult> GetPrefBases()
    {
        var ident = User.FindFirstValue("identificador");
        if (string.IsNullOrWhiteSpace(ident)) return Unauthorized();
        var u = await _db.Usuarios.FirstOrDefaultAsync(x => x.Identificador == ident);

        List<string> orden = new(), ocultas = new();
        if (!string.IsNullOrWhiteSpace(u?.PrefBases))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<PrefBasesDto>(u!.PrefBases!,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                orden = doc?.Orden ?? new();
                ocultas = doc?.Ocultas ?? new();
            }
            catch { /* pref corrupta: default vacío */ }
        }
        // disponibles = las bases que el usuario puede ver (para poblar la barra ANTES de
        // consultar y poder pre-elegir).
        return Ok(new { orden, ocultas, disponibles = PortalBases() });
    }

    public sealed record PrefBasesDto(List<string>? Orden, List<string>? Ocultas);

    [HttpPut("pref-bases")]
    public async Task<ActionResult> PutPrefBases([FromBody] PrefBasesDto dto)
    {
        var ident = User.FindFirstValue("identificador");
        if (string.IsNullOrWhiteSpace(ident)) return Unauthorized();
        var u = await _db.Usuarios.FirstOrDefaultAsync(x => x.Identificador == ident);
        if (u is null) return NotFound(new { mensaje = "Usuario no encontrado." });

        static List<string> Limpiar(List<string>? xs) => (xs ?? new())
            .Select(s => (s ?? "").Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        u.PrefBases = JsonSerializer.Serialize(new
        {
            orden = Limpiar(dto?.Orden),
            ocultas = Limpiar(dto?.Ocultas)
        });
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // GET /api/mi-cuenta/datos?cuit=  -> datos del cliente (desde BAS, por su CUIT).
    // Extranet: su propio CUIT. Staff: el ?cuit= del cliente elegido.
    [HttpGet("datos")]
    public async Task<ActionResult> Datos([FromQuery] string? cuit)
    {
        var (objetivo, err) = ResolverCuitObjetivo(cuit);
        if (objetivo is null) return BadRequest(new { mensaje = err });
        var codigo = EsStaff() ? null : User.FindFirstValue("codigoCliente");

        try
        {
            // Buscamos el cliente en las bases del usuario (la primera que lo tenga).
            ClienteBas? c = null;
            foreach (var bb in PortalBases())
            {
                c = await _clientes.BuscarPorCuitEnBaseAsync(bb, objetivo);
                if (c is not null) break;
            }
            if (c is null)
                return NotFound(new { mensaje = "No se encontraron los datos del cliente en BAS." });

            var dom = c.Domicilios.FirstOrDefault();
            object? domicilio = dom is null ? null : new
            {
                calle = string.Join(" ", new[] { dom.Domicilio1, dom.Domicilio2 }
                            .Where(x => !string.IsNullOrWhiteSpace(x))),
                localidad = dom.Localidad,
                codigoPostal = dom.CodigoPostal,
                provincia = dom.Provincia,
                telefono = dom.Telefono
            };

            return Ok(new
            {
                razonSocial = c.RazonSocial,
                nombreFantasia = c.NombreFantasia,
                cuit = string.IsNullOrWhiteSpace(c.NumeroImpositivo1) ? objetivo : c.NumeroImpositivo1,
                codigoCliente = string.IsNullOrWhiteSpace(codigo) ? c.Codigo : codigo,
                email = c.Email,
                paginaWeb = c.PaginaWeb,
                condicionImpositiva = c.TratImpositivo,
                domicilio
            });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { mensaje = "No se pudieron consultar tus datos en BAS: " + ex.Message });
        }
    }

    // GET /api/mi-cuenta/cuenta-corriente?fecha=yyyy-MM-dd (fecha opcional)
    // Estado de cuenta CONSOLIDADO del cliente logueado: trae la cuenta corriente
    // de cada base (BARK + PRUEBAB), resolviendo el codigo del cliente por CUIT en
    // cada una, y devuelve los movimientos mergeados y ordenados por fecha, cada
    // uno etiquetado con su base. El saldo de cada comprobante es sumable, asi que
    // el total y la columna acumulada combinan ambas bases naturalmente.
    [HttpGet("cuenta-corriente")]
    public async Task<ActionResult> CuentaCorriente([FromQuery] string? fecha, [FromQuery] string? cuit,
        [FromQuery] string? bases, CancellationToken ct = default)
    {
        var (objetivo, err) = ResolverCuitObjetivo(cuit);
        if (objetivo is null) return BadRequest(new { mensaje = err });

        var f = string.IsNullOrWhiteSpace(fecha)
            ? DateTime.Today.ToString("yyyy-MM-dd")
            : fecha.Trim();

        // Traemos cada base en paralelo, con error aislado por base. Sólo las bases pedidas
        // (tildadas en el front); sin el parámetro, todas las del usuario.
        var resultados = await Task.WhenAll(
            FiltrarBases(bases).Select(b => TraerEstadoBaseAsync(b, objetivo, f, ct)));

        var comprobantes = resultados
            .SelectMany(r => r.Comprobantes)
            .OrderBy(c => c.OrdenFecha)
            .ThenBy(c => c.Base, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Numero)
            .Select(c => new
            {
                baseNombre = c.Base,
                codigoCliente = c.CodigoCliente,
                razonSocial = c.RazonSocial,
                fecha = c.Fecha,
                tipo = c.Tipo,
                prefijo = c.Prefijo,
                numero = c.Numero,
                comprobante = $"{c.Prefijo}-{c.Numero}",
                vencimiento = c.Vencimiento,
                cuotas = c.Cuotas,
                total = c.Total,
                saldo = c.Saldo
            })
            .ToList();

        return Ok(new
        {
            totalSaldo = resultados.Sum(r => r.TotalSaldo),
            fechaActualizacion = resultados
                .Where(r => !string.IsNullOrWhiteSpace(r.FechaActualizacion))
                .Select(r => r.FechaActualizacion)
                .LastOrDefault(),
            bases = resultados.Select(r => new { baseNombre = r.Base, ok = r.Ok, error = r.Error }),
            comprobantes
        });
    }

    // Trae el estado de cuenta de UNA base, resolviendo el codigo del cliente por
    // CUIT en esa base. Nunca lanza: ante error devuelve el resultado marcado con
    // Ok=false y el mensaje, para no tumbar toda la consulta si una base falla.
    private async Task<EstadoBaseResultado> TraerEstadoBaseAsync(string baseNombre, string cuit, string fecha, CancellationToken ct)
    {
        try
        {
            // Todas las cuentas casa-central/independientes del CUIT en esta base.
            var cuentas = await _clientes.BuscarCuentasPorCuitEnBaseAsync(baseNombre, cuit, ct);
            if (cuentas.Count == 0)
                // El cliente no existe en esta base: no es error, simplemente no aporta movimientos.
                return new EstadoBaseResultado(baseNombre, true, null, 0m, null, new());

            var lista = new List<CompCtaCte>();
            decimal totalSaldo = 0m;
            string? fechaActualizacion = null;

            // Cada cuenta tiene su propia cuenta corriente; las traemos (secuencial,
            // BAS no tolera paralelo por base) y mergeamos, etiquetando cada
            // movimiento con el código de la cuenta a la que pertenece.
            foreach (var cuenta in cuentas)
            {
                var codigo = (cuenta.Codigo ?? "").Trim();
                if (codigo.Length == 0) continue;

                var estado = await _ctaCte.EstadoClienteEnBaseAsync(baseNombre, codigo, fecha, ct);
                var comps = estado?.Comprobantes ?? new();

                lista.AddRange(comps.Select(c => new CompCtaCte(
                    baseNombre,
                    codigo,
                    cuenta.RazonSocial,
                    ParseFecha(c.Fecha),
                    c.Fecha,
                    c.TipoComprobante,
                    c.Prefijo,
                    c.Numero,
                    c.Vencimientos.FirstOrDefault()?.FechaVencimiento,
                    c.Vencimientos.Count,
                    c.TotalCtaCte,
                    c.Saldo)));

                totalSaldo += comps.Sum(c => c.Saldo);
                if (!string.IsNullOrWhiteSpace(estado?.FechaActualizacion))
                    fechaActualizacion = estado.FechaActualizacion;
            }

            return new EstadoBaseResultado(baseNombre, true, null, totalSaldo, fechaActualizacion, lista);
        }
        catch (Exception ex)
        {
            return new EstadoBaseResultado(baseNombre, false, ex.Message, 0m, null, new());
        }
    }

    private static DateTime ParseFecha(string? s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : DateTime.MaxValue;

    private sealed record CompCtaCte(
        string Base, string? CodigoCliente, string? RazonSocial, DateTime OrdenFecha, string? Fecha, string? Tipo,
        string? Prefijo, int Numero, string? Vencimiento, int Cuotas, decimal Total, decimal Saldo);

    private sealed record EstadoBaseResultado(
        string Base, bool Ok, string? Error, decimal TotalSaldo, string? FechaActualizacion,
        List<CompCtaCte> Comprobantes);

    // GET /api/mi-cuenta/comprobante?tipo=&prefijo=&numero=&base=
    // Detalle (items) de UN comprobante en la base indicada. Verifica que sea del
    // cliente logueado (segun su codigo EN ESA base).
    [HttpGet("comprobante")]
    public async Task<ActionResult> Comprobante(
        [FromQuery] string tipo, [FromQuery] string prefijo, [FromQuery] string numero,
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery(Name = "cuit")] string? cuitParam)
    {
        var (objetivo, err) = ResolverCuitObjetivo(cuitParam);
        if (objetivo is null) return BadRequest(new { mensaje = err });

        if (string.IsNullOrWhiteSpace(tipo) || string.IsNullOrWhiteSpace(prefijo) || string.IsNullOrWhiteSpace(numero))
            return BadRequest(new { mensaje = "Faltan datos del comprobante." });

        var bn = PortalBases().FirstOrDefault(b =>
            string.Equals(b, (baseNombre ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (bn is null)
            return BadRequest(new { mensaje = "Base inválida." });

        try
        {
            // Códigos del cliente EN esa base (puede tener varias cuentas con el
            // mismo CUIT), para el control de acceso.
            var cuentas = await _clientes.BuscarCuentasPorCuitEnBaseAsync(bn, objetivo);
            var codigos = cuentas
                .Select(x => (x.Codigo ?? "").Trim())
                .Where(x => x.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (codigos.Count == 0)
                return StatusCode(403, new { mensaje = "No tenés cuenta en esta base." });

            var c = await _comprobantes.ConsultaVentaEnBaseAsync(bn, tipo, prefijo, numero);
            if (c is null)
                return NotFound(new { mensaje = "No se encontró el comprobante." });

            // Control de acceso: el comprobante tiene que ser de alguna de sus cuentas.
            var dueno = (c.CodClienteCtaCte ?? "").Trim();
            if (!codigos.Contains(dueno))
                return StatusCode(403, new { mensaje = "Este comprobante no corresponde a tu cuenta." });

            var items = c.LineasItem.Select(l => new
            {
                descripcion = l.Item,
                cantidad = l.Cantidad1,
                unidad = l.NroUnimed,
                precio = l.Precio,
                bonificacion = l.Bonificacion,
                iva = l.ImpIva,
                importe = l.Importe
            });

            // Resumen de impuestos: BAS los guarda por renglón, así que sumamos.
            var impuestos = new
            {
                netoGravado = c.LineasItem.Sum(l => l.ImpGravado),
                iva = c.LineasItem.Sum(l => l.ImpIva),
                ivaAdicional = c.LineasItem.Sum(l => l.ImpIvaNoi),
                percepcionIva = c.LineasItem.Sum(l => l.ImpPerIva),
                percepcionIibb = c.LineasItem.Sum(l => l.ImpPerIbr),
                internos = c.LineasItem.Sum(l => l.ImpInterno)
            };

            return Ok(new
            {
                baseNombre = bn,
                tipo = c.CodCmp,
                prefijo = c.Prefijo,
                numero = c.Numero,
                comprobante = $"{c.CodCmp} {c.Prefijo}-{c.Numero}".Trim(),
                fecha = c.Fecha,
                moneda = c.MonedaCtaCte,
                observacion = c.Observacion,
                impuestos,
                total = c.Total,
                saldo = c.Saldo,
                items
            });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { mensaje = "No se pudo consultar el comprobante en BAS: " + ex.Message });
        }
    }
}
