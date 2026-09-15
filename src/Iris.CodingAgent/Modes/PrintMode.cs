using Iris.Ai;
using Iris.Ai.Json;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Core.Extensions;
using Iris.CodingAgent.Utils;
using Iris.Extensions;

namespace Iris.CodingAgent.Modes;

/// <summary>Single-shot mode: send prompts, print the result (text) or every event (json), exit.</summary>
public static class PrintMode
{
    public static async Task<int> RunAsync(AgentSessionRuntime runtime, string mode, IReadOnlyList<string> messages, string? initialMessage, List<ImageContent>? initialImages, TextWriter stdout)
    {
        var exitCode = 0;
        var session = runtime.Session;
        IDisposable? subscription = null;
        var disposed = false;
        var writeLock = new object();

        void Write(string line)
        {
            lock (writeLock)
            {
                stdout.Write(line);
                stdout.Write('\n');
                stdout.Flush();
            }
        }

        async Task DisposeRuntimeAsync()
        {
            if (disposed) return;
            disposed = true;
            subscription?.Dispose();
            await runtime.DisposeAsync();
        }

        async Task RebindSessionAsync()
        {
            session = runtime.Session;
            await session.BindExtensionsAsync(new ExtensionBindings
            {
                Mode = mode == "json" ? ExtensionModes.Json : ExtensionModes.Print,
                OnError = err => Console.Error.WriteLine($"Extension error ({err.ExtensionPath}): {err.Error}"),
            });
            subscription?.Dispose();
            subscription = session.Subscribe(evt =>
            {
                if (mode == "json") Write(JsonEvents.ToJson(evt).ToJsonString(IrisJson.Options));
            });
        }

        runtime.SetRebindSession(_ => RebindSessionAsync());

        // SIGTERM / Ctrl+Break: kill tracked children and dispose before exiting.
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            ShellUtils.KillTrackedDetachedChildren();
            DisposeRuntimeAsync().ContinueWith(_ => Environment.Exit(143));
        });

        try
        {
            if (mode == "json" && session.SessionManager.Header is { } header)
            {
                Write(IrisJson.Serialize<FileEntry>(header));
            }

            await RebindSessionAsync();

            if (initialMessage is not null) await session.PromptAsync(initialMessage, new PromptOptions { Images = initialImages });
            foreach (var message in messages) await session.PromptAsync(message);

            if (mode == "text" && session.State.Messages.LastOrDefault() is AssistantMessage last)
            {
                if (last.StopReason is StopReason.Error or StopReason.Aborted)
                {
                    Console.Error.WriteLine(string.IsNullOrEmpty(last.ErrorMessage) ? $"Request {IrisJson.Serialize(last.StopReason).Trim('"')}" : last.ErrorMessage);
                    exitCode = 1;
                }
                else
                {
                    foreach (var text in last.Content.OfType<TextContent>()) Write(text.Text);
                }
            }
            return exitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            await DisposeRuntimeAsync();
            await stdout.FlushAsync();
        }
    }
}
