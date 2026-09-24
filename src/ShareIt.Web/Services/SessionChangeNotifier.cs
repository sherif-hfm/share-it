using ShareIt.Core.Contracts;

namespace ShareIt.Web.Services;

public sealed class SessionChangeNotifier : ISessionChangePublisher
{
    public event Action<Guid>? Changed;
    public void Publish(Guid sessionId) => Changed?.Invoke(sessionId);
}
