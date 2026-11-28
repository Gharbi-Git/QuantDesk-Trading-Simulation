namespace QuantDesk.Exchange.Operational;

// Explicitly started test-only worker. No automatic schema binding, no automatic
// restart after errors, and no silent skipping of unknown commands. Construct the
// flow with quantdesk_test. Run one worker per isolated test composition.
public sealed class OperationalRecoveryWorker
{
    private readonly OperationalTradingFlow flow;
    private readonly bool resumePrepared;
    private readonly int maxSteps;
    private readonly TimeSpan interval;
    private readonly SemaphoreSlim gate=new(1,1);
    public OperationalRecoveryWorker(OperationalTradingFlow flow,bool resumePreparedTestCommands=false,int maxSteps=32,TimeSpan? interval=null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if(maxSteps is <1 or >256) throw new ArgumentOutOfRangeException(nameof(maxSteps));
        var delay=interval??TimeSpan.FromMilliseconds(250);
        if(delay<TimeSpan.FromMilliseconds(20)||delay>TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        this.flow=flow;resumePrepared=resumePreparedTestCommands;this.maxSteps=maxSteps;this.interval=delay;
    }
    public async Task<int> RunPassAsync(CancellationToken ct=default)
    {
        if(!await gate.WaitAsync(0,ct)) throw new InvalidOperationException("Recovery pass already running.");
        try
        {
            var count=0;
            while(count<maxSteps)
            {
                ct.ThrowIfCancellationRequested();
                if(!await flow.RecoverOneAsync(resumePrepared,ct)) break;
                count++;
            }
            return count;
        }
        finally {gate.Release();}
    }
    // Errors propagate to caller/supervisor. Only requested cancellation is normal.
    // One bounded pass per interval avoids unbounded busy polling.
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                await RunPassAsync(ct);
                await Task.Delay(interval,ct);
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
    }
}
