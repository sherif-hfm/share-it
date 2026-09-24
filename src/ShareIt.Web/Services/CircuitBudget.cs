using Microsoft.AspNetCore.Components.Server.Circuits;
using ShareIt.Core.Configuration;

namespace ShareIt.Web.Services;

public sealed class CircuitRegistry(ShareItLimits limits)
{
    private readonly HashSet<string> active = [];
    public void Add(string id) { lock (active) { if (active.Count >= limits.MaxCircuits) throw new InvalidOperationException("Connection capacity reached."); active.Add(id); } }
    public void Remove(string id) { lock (active) active.Remove(id); }
}
public sealed class CircuitBudget(CircuitRegistry registry) : CircuitHandler
{
    private DateTime window = DateTime.UtcNow;
    private int count;
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct) { registry.Add(circuit.Id); return Task.CompletedTask; }
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct) { registry.Remove(circuit.Id); return Task.CompletedTask; }
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) => async context =>
    {
        if (DateTime.UtcNow - window > TimeSpan.FromMinutes(1)) { window = DateTime.UtcNow; count = 0; }
        if (++count > 240) throw new InvalidOperationException("Too many actions. Reconnect to continue.");
        await next(context);
    };
}
