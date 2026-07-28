namespace PortalClientes.Models;

// Registro de un echeq EMITIDO por API al Banco Credicoop. Su razón de ser es la
// IDEMPOTENCIA: evita emitir dos veces el mismo cheque. Antes de emitir un rango, el
// portal cruza los cheques de BAS contra esta tabla (por BaseNombre + NumeroCheque) y
// sólo emite los que no están.
//
// Sólo se persisten los echeqs que el banco ACEPTÓ (estado "Aceptada" / "Enviada a la
// firma"). Los rechazados/errores NO se guardan, así se pueden reintentar tras corregir
// el dato. El índice único (BaseNombre, NumeroCheque) garantiza el no-duplicado.
public class EmisionEcheq
{
    public int Id { get; set; }

    // Base BAS de la que salió el cheque (los números de cheque se repiten entre bases).
    public string BaseNombre { get; set; } = "";
    // Número de cheque de BAS (NUMEROEXT) que se mandó como numeroCheque al banco.
    public long NumeroCheque { get; set; }

    public string Cuit { get; set; } = "";
    public string Beneficiario { get; set; } = "";
    // Decimales/fechas como TEXT (mismo criterio que el resto de las entidades del portal).
    public string Monto { get; set; } = "0";
    public string FechaPago { get; set; } = "";       // yyyyMMdd (como se mandó al banco)

    // Idempotencia hacia el banco: idOrigen único generado por nosotros; idOperacion e
    // idCheque los devuelve el banco.
    public string IdOrigen { get; set; } = "";
    public long? IdOperacion { get; set; }
    public string? IdCheque { get; set; }

    // Estado devuelto por el banco ("Aceptada" / "Enviada a la firma").
    public string Estado { get; set; } = "";

    public string EmitidoPor { get; set; } = "";
    public DateTime EmitidoEn { get; set; }
}
