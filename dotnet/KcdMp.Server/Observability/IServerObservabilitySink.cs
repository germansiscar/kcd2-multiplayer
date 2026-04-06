namespace KcdMp.Server.Observability;

public interface IServerObservabilitySink
{
    void Emit(ServerObservableEvent observableEvent);
}

