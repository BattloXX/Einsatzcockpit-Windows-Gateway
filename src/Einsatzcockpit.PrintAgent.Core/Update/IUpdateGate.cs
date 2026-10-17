namespace Einsatzcockpit.PrintAgent.Core.Update;

public interface IUpdateGate
{
    bool IsUpdatePending { get; }
}

public sealed class NoUpdateGate : IUpdateGate
{
    public bool IsUpdatePending => false;
}
