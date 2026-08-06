using CompliDrop.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Test-only pipeline hook that lets a test abort a request MID-HANDLER, deterministically.
/// <para/>
/// A client abort (tab closed, phone drops signal) is a real production failure path, and on the two
/// upload endpoints it lands between the blob upload and the commit — the exact window whose
/// best-effort blob cleanup must still run. It cannot be reproduced by cancelling the
/// <c>HttpClient</c>'s token: that races the server's own unwinding, and the assertion would run
/// before (or after) the endpoint's <c>finally</c>. So instead this REPLACES
/// <see cref="HttpContext.RequestAborted"/> with a token the test controls, and hands the
/// <see cref="CancellationTokenSource"/> to <see cref="FakeBlobStorageService"/>, which cancels it the
/// instant the blob is stored. The ordering is then structural, not timed: upload → cancel → the next
/// <c>await ...(ct)</c> in the handler throws → <c>finally</c> → cleanup.
/// <para/>
/// TestServer's own request lifetime is untouched, so the request still completes normally: the
/// <c>OperationCanceledException</c> reaches <c>ExceptionHandlingMiddleware</c> and comes back as a
/// BODILESS 499 (#390 — it was the ordinary 500 envelope until then; an abort is not a server fault
/// and nobody is left to read an envelope) — which means the client's response arrives only AFTER the
/// handler's <c>finally</c> has run, and the test can assert on the blob store with no waiting.
/// <para/>
/// Inert unless the request carries <see cref="HeaderName"/>, so every other test is unaffected.
/// </summary>
public sealed class ClientAbortStartupFilter : IStartupFilter
{
    /// <summary>Opt-in header. Its presence, not its value, arms the abort.</summary>
    public const string HeaderName = "X-Test-Abort-After-Blob-Upload";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (!context.Request.Headers.ContainsKey(HeaderName)
                || context.RequestServices.GetService<IBlobStorageService>() is not FakeBlobStorageService blobs)
            {
                await nextMiddleware(context);
                return;
            }

            using var abort = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            context.RequestAborted = abort.Token;
            blobs.CancelRequestAfterUpload = abort;
            try
            {
                await nextMiddleware(context);
            }
            finally
            {
                blobs.CancelRequestAfterUpload = null;
            }
        });

        next(app);
    };
}
