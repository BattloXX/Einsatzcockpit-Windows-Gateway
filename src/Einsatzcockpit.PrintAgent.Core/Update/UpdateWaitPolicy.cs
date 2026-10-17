namespace Einsatzcockpit.PrintAgent.Core.Update;

public static class UpdateWaitPolicy
{
    public static UpdateWaitDecision Decide(bool hasActiveJobs, DateTimeOffset pendingSince,
        DateTimeOffset now)
    {
        if (!hasActiveJobs)
        {
            return UpdateWaitDecision.Install;
        }

        return now - pendingSince >= TimeSpan.FromMinutes(5)
            ? UpdateWaitDecision.Defer
            : UpdateWaitDecision.Wait;
    }
}

public enum UpdateWaitDecision
{
    Wait,
    Install,
    Defer
}
