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
    private readonly IHttpClientFactory _factory;
    private readonly BasAuthService _auth;

    // Ajustables segun lo que veamos en la prueba.
    private const int PageSize = 200;
    private const int MaxPaginas = 50; // tope de seguridad (~10.000 proveedores)

    public BasProveedoresService(IHttpClientFactory factory, BasAuthService auth)
    {
        _factory = factory;
        _auth = auth;
    }

    public async Task<ProveedorBas?> BuscarPorCuitAsync(string cuit, CancellationToken ct = default)
    {
        var objetivo = SoloDigitos(cuit);
        if (objetivo.Length == 0)
            return null;

        var token = await _auth.GetTokenAsync(ct);
        var http = _factory.CreateClient("bas");

        for (int page = 1; page <= MaxPaginas; page++)
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"/api/Proveedores?pageSize={PageSize}&pageNumber={page}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"BAS devolvio {(int)resp.StatusCode} listando proveedores. {err}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var lista = JsonSerializer.Deserialize<List<ProveedorBas>>(json, JsonOpts) ?? new();

            var match = lista.FirstOrDefault(p => SoloDigitos(p.NumeroImpositivo1 ?? "") == objetivo);
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
