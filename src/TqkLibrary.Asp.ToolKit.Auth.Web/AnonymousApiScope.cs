namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Marks "API calls inside this scope MUST be anonymous": <see cref="SessionBearerHandler"/> passes them through
    /// without the viewer's bearer and without refreshing.
    /// <para>
    /// Needed for caches shared by every viewer: a shared load (and its error) is handed to every waiting caller, so a
    /// loader carrying ONE viewer's bearer could store that viewer's private data in the shared cache, or hand that
    /// viewer's <see cref="SessionExpiredException"/> to everybody else.
    /// </para>
    /// <para>The flag lives in an <see cref="AsyncLocal{T}"/>: it flows with the async call only and never leaks to other requests.</para>
    /// </summary>
    public static class AnonymousApiScope
    {
        private static readonly AsyncLocal<bool> _active = new();

        /// <summary>True inside <see cref="RunAsync{T}"/> / <see cref="RunAsync"/>.</summary>
        public static bool IsActive => _active.Value;

        /// <summary>Runs <paramref name="call"/> with the anonymous flag on.</summary>
        public static async Task<T> RunAsync<T>(Func<Task<T>> call)
        {
            ArgumentNullException.ThrowIfNull(call);
            // Set inside this async method: the change is visible to `call` and is discarded when the method returns.
            _active.Value = true;
            return await call().ConfigureAwait(false);
        }

        /// <summary>Runs <paramref name="call"/> with the anonymous flag on.</summary>
        public static async Task RunAsync(Func<Task> call)
        {
            ArgumentNullException.ThrowIfNull(call);
            _active.Value = true;
            await call().ConfigureAwait(false);
        }
    }
}
