namespace ShareIt.Core.Contracts;

public interface ISessionChangePublisher
{
    void Publish(Guid sessionId);
}
