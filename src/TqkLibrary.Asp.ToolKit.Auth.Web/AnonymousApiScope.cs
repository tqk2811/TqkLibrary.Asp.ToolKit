namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Sends API calls anonymously through a client wired with <see cref="SessionBearerHandler"/>: inside
    /// <see cref="RunAsync{T}"/> / <see cref="RunAsync"/> the handler passes requests through untouched (no viewer
    /// session token, and a 401 is returned as it is instead of <see cref="SessionExpiredException"/>).
    /// <para>Use:</para>
    /// <list type="bullet">
    ///   <item>The app calls an endpoint anonymously only SOMETIMES, for example the loader of a cache shared by every
    ///   viewer: a shared load (and its error) is handed to every waiting caller, so a load carrying ONE viewer's
    ///   bearer could cache that viewer's private data or hand that viewer's <see cref="SessionExpiredException"/> to
    ///   everybody. Wrap the call INSIDE the loader, so whichever request starts the load runs it anonymously.
    ///   Endpoints that are ALWAYS anonymous (login, password reset) belong in
    ///   <see cref="WebAuthOptions.AnonymousPathSuffixes"/> instead.</item>
    /// </list>
    /// <para>
    /// The flag lives in an <see cref="AsyncLocal{T}"/> set inside an async method: it flows only to the code awaited
    /// inside the scope, concurrent requests (other users) never see it, and the caller sees the old value again when
    /// the method returns (the runtime restores the execution context; no reset code is needed). Work started inside
    /// the scope (<c>Task.Run</c>, fire-and-forget) keeps the flag even after the scope returns.
    /// </para>
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
