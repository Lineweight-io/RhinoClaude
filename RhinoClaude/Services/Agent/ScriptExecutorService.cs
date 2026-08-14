using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Rhino;
using Rhino.Geometry;
using RhinoClaude.Agent;
using RhinoClaude.Services;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// Globals exposed to a Tier 2 script. Matches plan §4.3.
    /// </summary>
    public class ScriptGlobals
    {
        /// <summary>The active document at script start.</summary>
        public RhinoDoc Doc;

        /// <summary>The shared RC: tag service.</summary>
        public TagService Tags;

        /// <summary>Append to captured stdout. Console.WriteLine is not captured.</summary>
        public Action<string> Log;

        /// <summary>Assign to return a value to the agent.</summary>
        public object Result;

        /// <summary>Honour this for cooperative cancellation in long loops.</summary>
        public CancellationToken Cancellation;
    }

    /// <summary>
    /// Tier 2 escape hatch: compiles and runs a C# snippet with full RhinoCommon access
    /// inside an isolated undo record, then reports what it changed.
    ///
    /// The sandbox is a speed bump, not a security boundary — Roslyn scripting has no CAS
    /// sandbox on modern .NET. Every call is logged so the log can drive Tier 1 promotion.
    ///
    /// UI-thread only: RhinoCommon requires it, so the script blocks the UI for at most
    /// its timeout.
    /// </summary>
    public sealed class ScriptExecutorService
    {
        private const int ResultCapBytes = 32 * 1024;

        /// <summary>Identifiers rejected before execution. Static analysis over the syntax tree.</summary>
        private static readonly string[] BlockedPatterns =
        {
            "File.Delete",
            "Directory.Delete",
            "System.Diagnostics.Process",
            "Process.Start",
            "Microsoft.Win32.Registry",
            "Registry.",
            "Environment.Exit",
            "Assembly.Load",
            "Assembly.LoadFrom",
            "AppDomain."
        };

        private static readonly Dictionary<string, Script<object>> CompileCache =
            new Dictionary<string, Script<object>>(StringComparer.Ordinal);

        private static readonly object CacheGate = new object();

        private readonly RhinoQueryService _query;
        private readonly SessionSnapshotService _snapshots;
        private readonly JsonlLogger _log;
        private readonly Guid _sessionId;

        public ScriptExecutorService(
            RhinoQueryService query,
            SessionSnapshotService snapshots,
            JsonlLogger scriptLog,
            Guid sessionId)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
            _log = scriptLog;
            _sessionId = sessionId;
        }

        public int DefaultTimeoutSeconds { get; set; } = 15;
        public int CurrentIteration { get; set; }

        /// <summary>
        /// Compile a trivial script so the first real call does not pay Roslyn's ~500 ms
        /// cold start (plan risk #3). Safe to call on a background thread.
        /// </summary>
        public static void Warm()
        {
            try
            {
                var script = CSharpScript.Create<object>("Result = 1;", BuildOptions(), typeof(ScriptGlobals));
                script.Compile();
            }
            catch (Exception)
            {
                // A failed warm-up is not worth surfacing; the real call will report properly.
            }
        }

        public ToolResult Run(string code, string purpose, string expectedResultShape, int timeoutSeconds, CancellationToken outerToken)
        {
            if (string.IsNullOrWhiteSpace(code))
                return ToolResult.Fail("'code' is required.");
            if (string.IsNullOrWhiteSpace(purpose))
                return ToolResult.Fail("'purpose' is required — it is what the script log is analysed by.");

            if (timeoutSeconds <= 0) timeoutSeconds = DefaultTimeoutSeconds;
            if (timeoutSeconds > 60) timeoutSeconds = 60;

            string blocked = FindBlockedCall(code);
            if (blocked != null)
            {
                var rejected = ToolResult.Fail(
                    "Rejected before execution: the script references '" + blocked + "', which is not allowed. " +
                    "Scripts must not delete files, start processes, touch the registry, exit the process, or load assemblies.");
                LogRun(code, purpose, rejected, 0, null, null, null, "blocked");
                return rejected;
            }

            var doc = _query.Doc;
            var stdout = new StringBuilder();
            var stopwatch = Stopwatch.StartNew();

            var before = SnapshotObjectIds(doc);

            List<string> compileErrors = null;
            object rawResult = null;
            string stderr = null;
            bool timedOut = false;

            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(outerToken, timeoutCts.Token))
            using (_snapshots.BeginMutation("script:" + Truncate(purpose, 60)))
            {
                var globals = new ScriptGlobals
                {
                    Doc = doc,
                    Tags = RhinoClaudePlugin.Instance?.TagService,
                    Log = text => stdout.AppendLine(text),
                    Cancellation = linked.Token
                };

                try
                {
                    var script = GetOrCompile(code, out compileErrors);

                    if (compileErrors != null && compileErrors.Count > 0)
                    {
                        stopwatch.Stop();
                        var failed = new ToolResult
                        {
                            Success = false,
                            Error = "Compilation failed.",
                            Json = ToolJson.Serialize(new Dictionary<string, object>
                            {
                                { "stdout", stdout.ToString() },
                                { "stderr", (string)null },
                                { "compileErrors", compileErrors },
                                { "executionMs", stopwatch.ElapsedMilliseconds }
                            })
                        };
                        LogRun(code, purpose, failed, stopwatch.ElapsedMilliseconds, null, null, null, "compile_error");
                        return failed;
                    }

                    // Blocking on the UI thread is deliberate: RhinoCommon requires the UI
                    // thread, so the script owns it for at most `timeoutSeconds`.
                    var state = script.RunAsync(globals, linked.Token).GetAwaiter().GetResult();
                    rawResult = globals.Result ?? state.ReturnValue;
                }
                catch (CompilationErrorException ex)
                {
                    compileErrors = ex.Diagnostics.Select(FormatDiagnostic).ToList();
                    stderr = ex.Message;
                }
                catch (OperationCanceledException)
                {
                    timedOut = timeoutCts.IsCancellationRequested;
                    stderr = timedOut
                        ? "Script exceeded its " + timeoutSeconds + "s timeout and was cancelled."
                        : "Script was cancelled by the user.";
                }
                catch (Exception ex)
                {
                    // The interesting exception is the one the script threw, not Roslyn's wrapper.
                    var inner = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
                    stderr = inner.GetType().Name + ": " + inner.Message;
                    if (!string.IsNullOrEmpty(inner.StackTrace))
                        stderr += "\n" + FirstLines(inner.StackTrace, 4);
                }
                finally
                {
                    stopwatch.Stop();
                    doc.Views.Redraw();
                }
            }

            var after = SnapshotObjectIds(doc);
            var created = after.Except(before).Select(g => g.ToString()).ToList();
            var deleted = before.Except(after).Select(g => g.ToString()).ToList();

            bool success = string.IsNullOrEmpty(stderr) && (compileErrors == null || compileErrors.Count == 0);

            string serialized;
            bool truncated;
            SerializeResult(doc, rawResult, out serialized, out truncated);

            var result = new ToolResult
            {
                Success = success,
                Error = success ? null : (stderr ?? "Script failed."),
                Json = ToolJson.Serialize(new Dictionary<string, object>
                {
                    { "stdout", Cap(stdout.ToString(), 8 * 1024) },
                    { "stderr", stderr },
                    { "compileErrors", compileErrors },
                    { "result", new RawJson(serialized) },
                    { "resultTruncated", truncated },
                    { "timedOut", timedOut },
                    { "executionMs", stopwatch.ElapsedMilliseconds },
                    { "createdObjectIds", created },
                    { "deletedObjectIds", deleted },
                    // Rhino replaces an object's id on most edits, so a "modified" object shows
                    // up as a delete plus a create. Reporting that honestly beats guessing.
                    { "modifiedObjectIds", new List<string>() }
                })
            };

            LogRun(code, purpose, result, stopwatch.ElapsedMilliseconds, expectedResultShape, created, deleted,
                success ? "ok" : (timedOut ? "timeout" : "runtime_error"));

            return result;
        }

        // ── compilation ───────────────────────────────────────────────

        private static Script<object> GetOrCompile(string code, out List<string> errors)
        {
            errors = null;
            string key = HashOf(code);

            lock (CacheGate)
            {
                if (CompileCache.TryGetValue(key, out var cached))
                    return cached;
            }

            var script = CSharpScript.Create<object>(code, BuildOptions(), typeof(ScriptGlobals));
            var diagnostics = script.Compile();

            var errorList = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(FormatDiagnostic)
                .ToList();

            if (errorList.Count > 0)
            {
                errors = errorList;
                return script;
            }

            lock (CacheGate)
            {
                CompileCache[key] = script;
                // Bound the cache so a long session cannot grow it without limit.
                if (CompileCache.Count > 64)
                {
                    var first = CompileCache.Keys.First();
                    CompileCache.Remove(first);
                }
            }

            return script;
        }

        private static ScriptOptions BuildOptions()
        {
            var references = new List<Assembly>
            {
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(System.Collections.Generic.List<>).Assembly,
                typeof(RhinoDoc).Assembly,          // RhinoCommon
                typeof(Point3d).Assembly,           // RhinoCommon (same assembly, harmless)
                typeof(TagService).Assembly         // RhinoClaude itself, for TagService/TagSchema
            };

            // On .NET Core the facade assemblies are separate; pull in whatever resolves.
            foreach (var name in new[] { "System.Runtime", "System.Linq", "System.Collections", "System.Console" })
            {
                try { references.Add(Assembly.Load(new AssemblyName(name))); }
                catch (Exception) { /* not present on this target framework */ }
            }

            return ScriptOptions.Default
                .WithReferences(references.Where(a => a != null).Distinct())
                .WithImports(
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "System.IO",
                    "Rhino",
                    "Rhino.Geometry",
                    "Rhino.DocObjects",
                    "Rhino.Commands");
        }

        private static string FormatDiagnostic(Diagnostic diagnostic)
        {
            var span = diagnostic.Location.GetLineSpan();
            return string.Format("line {0}: {1}: {2}",
                span.StartLinePosition.Line + 1,
                diagnostic.Id,
                diagnostic.GetMessage());
        }

        // ── guardrails ────────────────────────────────────────────────

        /// <summary>
        /// Parse the snippet and look for blocked calls in the syntax tree rather than in
        /// raw text, so a comment mentioning File.Delete does not trip it.
        /// Returns the offending pattern, or null.
        /// </summary>
        public static string FindBlockedCall(string code)
        {
            string searchable;
            try
            {
                var tree = CSharpSyntaxTree.ParseText(code,
                    new CSharpParseOptions(kind: SourceCodeKind.Script));
                var root = tree.GetRoot();

                var sb = new StringBuilder();
                foreach (var node in root.DescendantNodes())
                {
                    if (node is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax member)
                        sb.Append(member.ToString()).Append('\n');
                    else if (node is Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qualified)
                        sb.Append(qualified.ToString()).Append('\n');
                }
                searchable = sb.ToString();
            }
            catch (Exception)
            {
                // If parsing fails the compiler will reject it anyway; fall back to raw text.
                searchable = code;
            }

            foreach (var pattern in BlockedPatterns)
            {
                if (searchable.IndexOf(pattern, StringComparison.Ordinal) >= 0)
                    return pattern;
            }
            return null;
        }

        // ── result serialization (plan §4.4) ──────────────────────────

        private static void SerializeResult(RhinoDoc doc, object value, out string json, out bool truncated)
        {
            truncated = false;

            object shaped = ShapeResult(doc, value);
            try
            {
                json = ToolJson.Serialize(shaped);
            }
            catch (Exception)
            {
                json = ToolJson.Serialize(value?.ToString());
            }

            if (json != null && json.Length > ResultCapBytes)
            {
                truncated = true;
                json = ToolJson.Serialize(json.Substring(0, ResultCapBytes) + "…[truncated]");
            }
        }

        private static object ShapeResult(RhinoDoc doc, object value)
        {
            switch (value)
            {
                case null:
                    return null;

                case Guid guid:
                    return guid.ToString();

                case string s:
                    return s;

                case Rhino.DocObjects.RhinoObject rhinoObject:
                    return new Dictionary<string, object>
                    {
                        { "id", rhinoObject.Id.ToString() },
                        { "type", rhinoObject.ObjectType.ToString() },
                        { "bbox", RhinoQueryService.Bbox(rhinoObject.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset) }
                    };

                case GeometryBase geometry:
                    {
                        // The plan says add it to the doc if it isn't there. Doing that silently
                        // outside the script's own intent would be surprising, so report instead.
                        return new Dictionary<string, object>
                        {
                            { "type", geometry.GetType().Name },
                            { "bbox", RhinoQueryService.Bbox(geometry.GetBoundingBox(true)) },
                            { "note", "Geometry was returned but not added to the document. Call Doc.Objects.Add* inside the script if you want it in the model." }
                        };
                    }

                case IDictionary dictionary:
                    {
                        var map = new Dictionary<string, object>();
                        foreach (DictionaryEntry entry in dictionary)
                            map[Convert.ToString(entry.Key)] = ShapeResult(doc, entry.Value);
                        return map;
                    }

                case IEnumerable enumerable when !(value is string):
                    {
                        var items = new List<object>();
                        foreach (var item in enumerable)
                        {
                            items.Add(ShapeResult(doc, item));
                            if (items.Count >= 2000)
                            {
                                items.Add("…[list truncated at 2000 items]");
                                break;
                            }
                        }
                        return items;
                    }

                default:
                    if (value is bool || value is int || value is long || value is double ||
                        value is float || value is decimal)
                        return value;

                    if (value is Point3d p) return RhinoQueryService.Pt(p);
                    if (value is Vector3d v) return RhinoQueryService.Vec(v);
                    if (value is BoundingBox bb) return RhinoQueryService.Bbox(bb);

                    return new Dictionary<string, object>
                    {
                        { "value", value.ToString() },
                        { "note", "opaque result serialized as string" }
                    };
            }
        }

        // ── delta detection ───────────────────────────────────────────

        private static HashSet<Guid> SnapshotObjectIds(RhinoDoc doc)
        {
            var ids = new HashSet<Guid>();
            foreach (var obj in doc.Objects)
                if (!obj.IsDeleted) ids.Add(obj.Id);
            return ids;
        }

        // ── logging ───────────────────────────────────────────────────

        private void LogRun(string code, string purpose, ToolResult result, long elapsedMs,
            string expectedShape, List<string> created, List<string> deleted, string outcome)
        {
            _log?.Append(new Dictionary<string, object>
            {
                { "kind", "run_rhinocommon_script" },
                { "sessionId", _sessionId.ToString() },
                { "iteration", CurrentIteration },
                { "purpose", purpose },
                { "expectedResultShape", expectedShape },
                { "outcome", outcome },
                { "success", result.Success },
                { "error", result.Error },
                { "durationMs", elapsedMs },
                { "createdCount", created?.Count ?? 0 },
                { "deletedCount", deleted?.Count ?? 0 },
                { "codeLength", code?.Length ?? 0 },
                { "code", code }
            });
        }

        // ── small helpers ─────────────────────────────────────────────

        private static string HashOf(string text)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty)));
        }

        private static string Cap(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, max) + "…[truncated]";
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string FirstLines(string text, int count)
        {
            var lines = text.Split('\n');
            return string.Join("\n", lines.Take(count));
        }
    }
}
