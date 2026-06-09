namespace PortalClientes.Bas;

// Proceso en segundo plano para la caché del padrón:
//  1) Al arrancar, lee del disco lo guardado en corridas anteriores (instantáneo).
//  2) Refresca contra BAS solo lo que esté vencido (más de 6 hs) o falte.
//  3) Repite el refresco cada 6 hs.
// No bloquea el arranque del servidor.
public class BasCacheLoader : BackgroundService
{
    private readonly BasCacheRefresher _refresher;
    private readonly ILogger<BasCacheLoader> _log;
    private readonly TimeSpan _intervalo = TimeSpan.FromHours(6);

    public BasCacheLoader(BasCacheRefresher refresher, ILogger<BasCacheLoader> log)
    {
        _refresher = refresher;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (TaskCanceledException) { return; }

        // 1) Padrón disponible al instante desde disco.
        _refresher.CargarDesdeDisco();

        // 2) y 3) Refresco periódico (la primera vez, solo lo vencido/faltante).
        while (!stoppingToken.IsCancellationRequested)
        {
            _log.LogInformation("Revisando padrón BAS (refresca lo vencido)…");
            await _refresher.RefrescarTodoAsync(stoppingToken, soloVencidos: true, edadMaxima: _intervalo);

            try { await Task.Delay(_intervalo, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }
}
