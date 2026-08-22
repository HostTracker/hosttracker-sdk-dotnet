using System;
using Xunit;

namespace HostTracker.Sdk.Tests
{
    /// <summary>
    /// A test that talks to a real API instance. Opt in by setting <c>HT_BASE_URL</c> (and
    /// <c>HT_TOKEN</c> or <c>HT_TOKEN_FILE</c> for the authenticated ones); without it the test is
    /// reported as skipped rather than failing on a machine with no API to reach.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(LiveEnvironment.BaseUrl))
                Skip = "Set HT_BASE_URL to run the live smoke tests.";
        }
    }
}
