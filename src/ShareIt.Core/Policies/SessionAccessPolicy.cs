using ShareIt.Core.DTOs;
using ShareIt.Core.Models;

namespace ShareIt.Core.Policies;

public static class SessionAccessPolicy
{
    public static void Require(SharedSession session, Caller caller, DateTime now, bool write = false)
    {
        var granted = caller.ReadSessionId == session.Id ||
            (caller.BrowserId != null && session.Grants.Any(g => g.BrowserId == caller.BrowserId));
        if (!granted || (write && caller.IsReader))
            throw new ShareItException("access_denied", "Join this session to continue.", 403);
        if (session.Status != SessionStatus.Active || session.ExpiresAtUtc <= now)
            throw new ShareItException("session_closed", "This session has ended or expired.", 410);
    }
}
