using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rhino;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Resolves a tool_use block to its handler and runs it on Rhino's UI thread.
    ///
    /// RhinoCommon is UI-thread only, and the agent loop runs on a background task, so
    /// every invocation hops threads here. Exceptions become structured error results
    /// rather than propagating — a tool that throws should teach the model something,
    /// not kill the turn.
    /// </summary>
    public sealed class ToolDispatcher
    {
        private readonly ToolRegistry _registry;

        public ToolDispatcher(ToolRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public async Task<ToolInvocation> InvokeAsync(ToolUseBlock toolUse, CancellationToken cancellationToken)
        {
            var invocation = new ToolInvocation
            {
                ToolUseId = toolUse.Id,
                ToolName = toolUse.Name,
                InputJson = toolUse.InputJson
            };

            var stopwatch = Stopwatch.StartNew();

            if (!_registry.TryGet(toolUse.Name, out var tool))
            {
                stopwatch.Stop();
                invocation.ElapsedMs = stopwatch.ElapsedMilliseconds;
                invocation.Result = ToolResult.Fail(
                    "Unknown tool '" + toolUse.Name + "'. Available tools: " +
                    string.Join(", ", _registry.Names));
                return invocation;
            }

            invocation.TerminatesTurn = tool.TerminatesTurn;

            if (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                invocation.ElapsedMs = stopwatch.ElapsedMilliseconds;
                invocation.Result = ToolResult.Fail("Cancelled before the tool was dispatched.");
                return invocation;
            }

            JsonElement input;
            try
            {
                input = toolUse.ParseInput();
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                invocation.ElapsedMs = stopwatch.ElapsedMilliseconds;
                invocation.Result = ToolResult.Fail("Could not parse tool input as JSON: " + ex.Message);
                return invocation;
            }

            try
            {
                invocation.Result = await RunOnUiThreadAsync(
                    () => tool.Handler(input, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                invocation.Result = ToolResult.Fail("Cancelled while the tool was running.");
            }
            catch (Exception ex)
            {
                invocation.Result = ToolResult.Fail(
                    ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                stopwatch.Stop();
                invocation.ElapsedMs = stopwatch.ElapsedMilliseconds;
            }

            invocation.Result = invocation.Result ?? ToolResult.Fail("Tool returned no result.");
            return invocation;
        }

        /// <summary>
        /// Marshal onto Rhino's main thread and await the result. Exceptions are captured
        /// and rethrown on the calling thread rather than being swallowed by the UI pump.
        /// </summary>
        public static Task<T> RunOnUiThreadAsync<T>(Func<T> action)
        {
            var tcs = new TaskCompletionSource<T>();

            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }));

            return tcs.Task;
        }

        public static Task RunOnUiThreadAsync(Action action)
        {
            return RunOnUiThreadAsync<object>(() => { action(); return null; });
        }
    }
}
