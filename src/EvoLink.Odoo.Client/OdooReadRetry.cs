namespace EvoLink.Odoo.Client
{
    /// <summary>
    /// Retries of a paged read when Odoo is unavailable (<see cref="OdooUnavailableException"/>): 502 of a worker killed on a too
    /// large page, network failure, timeout. The delay doubles at each attempt (5, 10, 20 s by default).
    /// </summary>
    public sealed class OdooReadRetry
    {
        /// <summary>No retry: the first failure is thrown.</summary>
        public static OdooReadRetry None { get; } = new() { Attempts = 0 };

        /// <summary>Attempts after a failure of the same page (0 = none).</summary>
        public int Attempts { get; init; } = 3;

        /// <summary>Delay before the first retry, doubled at each following one.</summary>
        public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>Halves the page after a failure (down to <see cref="MinPageSize"/>), and keeps that size for the next pages.</summary>
        public bool ShrinkPage { get; init; } = true;

        public int MinPageSize { get; init; } = 10;

        /// <summary>Called before each retry (log).</summary>
        public Action<OdooRetryInfo>? OnRetry { get; init; }

        /// <summary>Wait between two attempts, replaceable by the tests.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

        internal TimeSpan DelayOf(int failure)
            => TimeSpan.FromTicks(Delay.Ticks * (1L << Math.Min(failure - 1, 10)));
    }

    /// <param name="Attempt">Number of the retry (1 = first).</param>
    /// <param name="PageSize">Page size used by the retry.</param>
    public sealed record OdooRetryInfo(int Attempt, int MaxAttempts, int PageSize, OdooUnavailableException Error);
}
