namespace KingbaseConnector.Protocol;

internal static class PgPasswordProvider
{
    internal static void Validate(KingbaseConnectionStringBuilder settings, KingbasePasswordProvider? provider)
    {
        if (provider is not null && settings.ContainsKey("Password"))
            throw new ArgumentException("An explicit Password and a password provider cannot be configured together.");
    }

    internal static async Task<string> GetAsync(KingbasePasswordProvider provider, KingbaseConnectionStringBuilder settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var context = new KingbasePasswordContext(settings);
        // Never run user code under pool locks or on a synchronous Open caller's synchronization context.
        var work = Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            var supplied = provider(context, token);
            if (supplied is null) throw new KingbasePasswordProviderException();
            return await supplied.ConfigureAwait(false);
        }, token);
        try
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetResult(true)))
            {
                if (await Task.WhenAny(work, canceled.Task).ConfigureAwait(false) != work)
                {
                    // A provider that ignores cancellation may finish later; it must not own I/O or leak a fault.
                    _ = work.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    token.ThrowIfCancellationRequested();
                }
            }
            var password = await work.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (password is null) throw new KingbasePasswordProviderException();
            PgFrontendMessages.PasswordTextLength(password);
            PgPasswordAuthentication.Md5InputLengths(settings.Username, password);
            return password;
        }
        catch
        {
            token.ThrowIfCancellationRequested();
            // User exceptions and returned values may contain credentials; do not retain them as InnerException.
            throw new KingbasePasswordProviderException();
        }
    }
}
