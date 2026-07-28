using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using PortalClientes.Data;
using PortalClientes.Models;

namespace PortalClientes.Bas;

// E-Cheques: consulta de cheques emitidos de una chequera, para exportar al formato de
// e-cheques del banco. NO va por el WebAPI de BAS (no expone esta consulta): se conecta
// DIRECTO al SQL Server de la base (servidor/base en ConfiguracionBase; usuario de SOLO
// LECTURA por variable de entorno). Reimplementa la misma query que el Echeques.py.
public class BasEchequesService
{
    private readonly PortalDbContext _db;
    private readonly IConfiguration _cfg;

    public BasEchequesService(PortalDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    // Una fila del export (mismos campos y constantes que el .xls que espera el banco).
    public sealed record ChequeRow(
        long NumEcheq, string Beneficiario, string TipoCuiCdi, string NroCuiCdi, decimal Importe,
        string FechaPago, string Concepto, string MotivoPago, string TipoCheque, int Caracter, int Modo,
        string Mail, string CodProveedor);

    // La misma consulta que Echeques.py, parametrizada. Cheques de la CHEQUERA + BANCO en el
    // rango de FECHA (y opcional rango de número), con beneficiario/CUIT/importe/vto/mail.
    private const string Sql = @"
        SELECT c.NUMEROEXT, v.Alaorden, ct.NRODOC1, v.Importe, c.FECHAVTO, cc.EMAIL, ct.CODCTACTE
        FROM dbo.CHEQUES c
        INNER JOIN dbo.VISTACHEQUES v ON c.NUMEROEXT = v.NumeroExt
        INNER JOIN dbo.CTACTES ct ON (v.cueprefi = ct.CUEPREFI) AND (v.codctacte = ct.CODCTACTE)
        INNER JOIN dbo.CTACTESCONT cc ON (ct.CUEPREFI = cc.CUEPREFI) AND (ct.CODCTACTE = cc.CODCTACTE)
        WHERE cc.EMAIL <> @emailPropio
          AND CAST(c.FECHA AS DATE) >= @desde
          AND CAST(c.FECHA AS DATE) <= @hasta
          AND LTRIM(RTRIM(CAST(c.CODCTABCO AS VARCHAR))) = @banco
          AND LTRIM(RTRIM(CAST(c.CHEQUERA AS VARCHAR))) = @chequera
          AND cc.COBPAG = 1";

    public async Task<IReadOnlyList<ChequeRow>> ConsultarAsync(
        string baseNombre, DateOnly desde, DateOnly hasta, string banco, string chequera,
        string? chqDesde, string? chqHasta, CancellationToken ct = default)
    {
        var cfg = await _db.ConfiguracionesBase.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Nombre == baseNombre, ct)
            ?? throw new InvalidOperationException($"Base desconocida: {baseNombre}.");
        if (string.IsNullOrWhiteSpace(cfg.SqlServidor) || string.IsNullOrWhiteSpace(cfg.SqlBase))
            throw new InvalidOperationException($"La base '{baseNombre}' no tiene configurada la conexión SQL para e-cheques.");

        var sql = Sql;
        var conRango = !string.IsNullOrWhiteSpace(chqDesde) && !string.IsNullOrWhiteSpace(chqHasta);
        if (conRango) sql += "\n          AND c.NUMEROEXT BETWEEN @chqDesde AND @chqHasta";

        var filas = new List<ChequeRow>();
        await using var con = new SqlConnection(ConnStr(cfg));
        await con.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, con) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@emailPropio", cfg.SqlEmailPropio ?? "");
        cmd.Parameters.AddWithValue("@desde", desde.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@hasta", hasta.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@banco", banco);
        cmd.Parameters.AddWithValue("@chequera", chequera);
        if (conRango)
        {
            cmd.Parameters.AddWithValue("@chqDesde", chqDesde!);
            cmd.Parameters.AddWithValue("@chqHasta", chqHasta!);
        }

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            // NUMEROEXT (numEcheq): descartamos los que no son número o son 0 (igual que el .py).
            if (!long.TryParse(Str(rd, 0), out var num) || num == 0) continue;

            filas.Add(new ChequeRow(
                num,
                Str(rd, 1),                                   // Alaorden -> beneficiario
                "CUIT",                                       // tipoCuiCdi (constante)
                Str(rd, 2),                                   // NRODOC1 -> nroCuiCdi
                Dec(rd, 3),                                   // Importe
                Fecha(rd, 4),                                 // FECHAVTO -> fechaPago (dd/MM/yyyy)
                "var", "prov", "ECHD", 1, 1,                  // constantes del formato del banco
                Str(rd, 5),                                   // EMAIL -> mail
                Str(rd, 6)));                                 // CODCTACTE -> código de proveedor en BAS
        }

        // Dedup por número (primero) y orden ascendente, igual que el .py.
        return filas
            .GroupBy(f => f.NumEcheq).Select(g => g.First())
            .OrderBy(f => f.NumEcheq)
            .ToList();
    }

    // Genera el .xls binario (BIFF, formato que espera el banco) idéntico al que hacía el
    // Echeques.py con xlwt: importe con formato '0.00'; numEcheq / nroCuiCdi / carácter / modo
    // como ENTEROS; el resto como texto; anchos de columna aprox. según el contenido.
    private static readonly string[] Columnas =
    {
        "numEcheq", "beneficiario", "tipoCuiCdi", "nroCuiCdi", "importe", "fechaPago",
        "concepto", "motivoPago", "tipoCheque", "carácter", "modo", "mail"
    };

    public static byte[] ArmarXls(IReadOnlyList<ChequeRow> filas)
    {
        var wb = new HSSFWorkbook();
        var sh = wb.CreateSheet("E-Cheques");
        var estiloImporte = wb.CreateCellStyle();
        estiloImporte.DataFormat = wb.CreateDataFormat().GetFormat("0.00");

        var head = sh.CreateRow(0);
        for (int c = 0; c < Columnas.Length; c++) head.CreateCell(c).SetCellValue(Columnas[c]);

        for (int i = 0; i < filas.Count; i++)
        {
            var f = filas[i];
            var r = sh.CreateRow(i + 1);
            r.CreateCell(0).SetCellValue((double)f.NumEcheq);
            r.CreateCell(1).SetCellValue(f.Beneficiario);
            r.CreateCell(2).SetCellValue(f.TipoCuiCdi);
            var cCuit = r.CreateCell(3);
            if (long.TryParse(f.NroCuiCdi, out var cuit)) cCuit.SetCellValue((double)cuit);
            else cCuit.SetCellValue(f.NroCuiCdi);
            var cImp = r.CreateCell(4);
            cImp.SetCellValue((double)f.Importe);
            cImp.CellStyle = estiloImporte;
            r.CreateCell(5).SetCellValue(f.FechaPago);
            r.CreateCell(6).SetCellValue(f.Concepto);
            r.CreateCell(7).SetCellValue(f.MotivoPago);
            r.CreateCell(8).SetCellValue(f.TipoCheque);
            r.CreateCell(9).SetCellValue((double)f.Caracter);
            r.CreateCell(10).SetCellValue((double)f.Modo);
            r.CreateCell(11).SetCellValue(f.Mail);
        }

        for (int c = 0; c < Columnas.Length; c++)
        {
            int max = Columnas[c].Length;
            foreach (var f in filas)
            {
                var s = c switch
                {
                    0 => f.NumEcheq.ToString(CultureInfo.InvariantCulture),
                    3 => f.NroCuiCdi,
                    4 => f.Importe.ToString("0.00", CultureInfo.InvariantCulture),
                    9 => f.Caracter.ToString(CultureInfo.InvariantCulture),
                    10 => f.Modo.ToString(CultureInfo.InvariantCulture),
                    1 => f.Beneficiario, 2 => f.TipoCuiCdi, 5 => f.FechaPago, 6 => f.Concepto,
                    7 => f.MotivoPago, 8 => f.TipoCheque, _ => f.Mail
                };
                if (s.Length > max) max = s.Length;
            }
            sh.SetColumnWidth(c, Math.Min(Math.Max(max + 3, 10), 255) * 256);
        }

        using var ms = new MemoryStream();
        wb.Write(ms);
        return ms.ToArray();
    }

    // Bases que tienen la config de e-cheques COMPLETA (server + base + mail propio + credencial
    // usable, ya sea de la config o de la variable de entorno). Sólo estas se ofrecen en el front.
    public async Task<IReadOnlyList<string>> BasesConfiguradasAsync(CancellationToken ct = default)
    {
        var envUser = _cfg["SqlConsultas:User"] ?? "";
        var envPass = _cfg["SqlConsultas:Password"] ?? "";
        var cfgs = await _db.ConfiguracionesBase.AsNoTracking().ToListAsync(ct);
        return cfgs.Where(c => EstaConfigurada(c, envUser, envPass))
            .Select(c => c.Nombre)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool EstaConfigurada(ConfiguracionBase c, string envUser, string envPass)
    {
        var user = !string.IsNullOrWhiteSpace(c.SqlUsuario) ? c.SqlUsuario : envUser;
        var pass = !string.IsNullOrWhiteSpace(c.SqlClave) ? c.SqlClave : envPass;
        return !string.IsNullOrWhiteSpace(c.SqlServidor)
            && !string.IsNullOrWhiteSpace(c.SqlBase)
            && !string.IsNullOrWhiteSpace(c.SqlEmailPropio)
            && !string.IsNullOrWhiteSpace(user)
            && !string.IsNullOrWhiteSpace(pass);
    }

    private string ConnStr(ConfiguracionBase cfg)
    {
        // Usuario/clave read-only: primero la config de la base; si está vacía, la variable
        // de entorno genérica (SqlConsultas__User / SqlConsultas__Password).
        var user = !string.IsNullOrWhiteSpace(cfg.SqlUsuario) ? cfg.SqlUsuario : (_cfg["SqlConsultas:User"] ?? "");
        var pass = !string.IsNullOrWhiteSpace(cfg.SqlClave)   ? cfg.SqlClave   : (_cfg["SqlConsultas:Password"] ?? "");
        var b = new SqlConnectionStringBuilder
        {
            DataSource = cfg.SqlServidor,
            InitialCatalog = cfg.SqlBase,
            UserID = user,
            Password = pass,
            TrustServerCertificate = true,          // SQL local, sin cert público
            ConnectTimeout = 8                       // base caída/inalcanzable → falla rápido
        };
        return b.ConnectionString;
    }

    // ---- lectura tolerante de columnas ----
    private static string Str(SqlDataReader rd, int i) => rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString()?.Trim() ?? "";
    // Importe: convertimos DIRECTO del valor numérico (decimal/money/float) SIN pasar por
    // texto. Hacer GetValue().ToString() usa la cultura del server (es-AR → coma decimal) y
    // al reparsear con InvariantCulture la coma se toma como separador de miles → cifras
    // infladas. Convert.ToDecimal sobre el valor tipado no tiene ese problema.
    private static decimal Dec(SqlDataReader rd, int i)
    {
        if (rd.IsDBNull(i)) return 0m;
        try { return Convert.ToDecimal(rd.GetValue(i), CultureInfo.InvariantCulture); }
        catch { return 0m; }
    }
    private static string Fecha(SqlDataReader rd, int i)
        => rd.IsDBNull(i) || !DateTime.TryParse(rd.GetValue(i)?.ToString(), out var f) ? "" : f.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
