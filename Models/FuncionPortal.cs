namespace PortalClientes.Models;

// Una "función" del portal de clientes: una consulta que aparece en el menú.
// La LÓGICA de cada función (su código JS en el front + su endpoint en el back)
// vive en el código; esta tabla sólo gobierna la METADATA editable desde la
// intranet: etiqueta visible, orden, a qué público aparece y si está activa.
//
// La Clave es el vínculo con el código (el catálogo IMPLEMENTACIONES del front,
// keyed por esta misma clave). Por eso NO se crea ni se edita desde la intranet:
// se siembra por código al publicar cada función nueva. Así es imposible que una
// fila apunte a un código inexistente.
public class FuncionPortal
{
    public int Id { get; set; }

    // Identificador técnico, único. Debe coincidir exacto con la implementación
    // del front (ej. "cuenta", "datos"). Se siembra por código, no se tipea a mano.
    public string Clave { get; set; } = "";

    // Texto que ve el usuario en el menú (editable desde la intranet).
    public string Etiqueta { get; set; } = "";

    // Orden de aparición en el menú (menor primero).
    public int Orden { get; set; } = 0;

    // A qué público le aparece: "externo" (clientes por internet), "interno"
    // (personal habilitado) o "ambos".
    public string Audiencia { get; set; } = "ambos";

    // Si está apagada, no aparece en el menú de nadie (sin borrar la fila).
    public bool Activa { get; set; } = true;

    // ---- Acceso para usuarios INTERNOS (independiente de los "Permisos" de intranet) ----
    // Los EXTERNOS acceden solo por audiencia (externo/ambos). Para los INTERNOS, el acceso
    // se asigna acá, desde "Programas para el Portal":
    //   - TodosLosInternos = true  -> la usan todos los internos (función de uso general).
    //   - TodosLosInternos = false -> solo los internos listados en UsuariosAsignados
    //     (identificador de login). Ideal para funciones sensibles (ej. e-cheques).
    // Un interno accede si: es admin · o TodosLosInternos · o está en UsuariosAsignados.
    public bool TodosLosInternos { get; set; } = true;
    public List<string> UsuariosAsignados { get; set; } = new();
}
