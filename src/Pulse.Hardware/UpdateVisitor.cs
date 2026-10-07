using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;

namespace Pulse.Hardware;

/// <summary>Refreshes every hardware node (including sub-hardware). Per-hardware failures are logged and skipped.</summary>
internal sealed class UpdateVisitor : IVisitor
{
    private readonly ILogger _logger;

    public UpdateVisitor(ILogger logger) => _logger = logger;

    public void VisitComputer(IComputer computer) => computer.Traverse(this);

    public void VisitHardware(IHardware hardware)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update failed for hardware {Hardware} ({Identifier})", hardware.Name, hardware.Identifier);
        }

        foreach (var sub in hardware.SubHardware)
        {
            sub.Accept(this);
        }
    }

    public void VisitSensor(ISensor sensor) { }

    public void VisitParameter(IParameter parameter) { }
}
