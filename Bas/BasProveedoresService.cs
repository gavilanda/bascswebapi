using System.Net.Http.Headers;
using System.Text.Json;

namespace PortalClientes.Bas;

// Consultas al maestro de Proveedores de BAS.
//
// OJO: el WebAPI de BAS NO tiene busqueda de proveedor por documento (CUIT),
// solo lista paginada (pageSize/pageNumber). Asi que para resolver por CUIT
// recorremos las paginas y filtramos nosotros por NumeroImpositivo1.
// Es mas pesado que la busqueda de cliente: cuantos mas proveedores haya,
// mas paginas hay que traer. Esta es justamente la prueba de "si vale la pena".
public class BasProveedoresService
{
    private readonly BasAuthService _auth;

    // Ajustables segun lo que veamos en la prueba.
    private const int PageSize = 200;
    private const int MaxPaginas = 50; // tope de seguridad (~10.000 proveedores)

    public BasProveedoresService(BasAuthService auth)
    {
        _auth = auth;
    }

    public async Task<ProveedorBas?> BuscarPorCuitAsync(string cuit, CancellationToken ct = default)
    {
        var objetivo = SoloDigitos(cuit);
        if (objetivo.Length == 0)
            return null;

        for (int page = 1; page <= MaxPaginas; page++)
        {
            int p = page;   // capturado por el lambda del reintento
            var json = await _auth.EnviarConReintentoAuthAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"/api/Proveedores?pageSize={PageSize}&pageNumber={p}"), ct);
            if (json is null) break;   // 404/204 → no hay (más) proveedores

            var lista = JsonSerializer.Deserialize<List<ProveedorBas>>(json, JsonOpts) ?? new();

            var match = lista.FirstOrDefault(pr => SoloDigitos(pr.NumeroImpositivo1 ?? "") == objetivo);
            if (match is not null)
                return match;

            // Si la pagina vino incompleta, era la ultima: no hay mas.
            if (lista.Count < PageSize)
                break;
        }

        return null;
    }

    private static string SoloDigitos(string s)
        => new string(s.Where(char.IsDigit).ToArray());

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
