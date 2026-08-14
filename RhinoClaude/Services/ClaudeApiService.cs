using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RhinoClaude.Schema;

namespace RhinoClaude.Services
{
    /// <summary>
    /// Service that communicates with the Anthropic Messages API.
    /// Uses raw HttpClient — no external SDK dependency.
    /// </summary>
    public class ClaudeApiService
    {
        private const string ApiUrl = "https://api.anthropic.com/v1/messages";
        private const string DefaultModel = "claude-sonnet-4-20250514";
        private const string ApiVersion = "2023-06-01";
        private const int DefaultMaxTokens = 16384;

        private static readonly HttpClient _httpClient = new HttpClient();
        private string _apiKey;

        /// <summary>Whether a valid API key has been configured.</summary>
        public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

        /// <summary>The system prompt that gives Claude context about the Rhino environment.</summary>
        public string SystemPrompt { get; set; } = BuildSystemPrompt();

        /// <summary>Set the Anthropic API key.</summary>
        public void SetApiKey(string apiKey)
        {
            _apiKey = apiKey?.Trim();
        }

        /// <summary>
        /// Send a message to Claude and return the response text.
        /// Optionally include scene context that gets prepended to the user message.
        /// </summary>
        public async Task<string> SendMessageAsync(
            string userMessage,
            string sceneContext = null,
            List<ConversationMessage> conversationHistory = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
                return "Error: No API key configured. Run the 'ClaudeSetKey' command first.";

            try
            {
                // Build the messages array
                var messages = new List<object>();

                // Include prior conversation history if provided
                if (conversationHistory != null)
                {
                    foreach (var msg in conversationHistory)
                    {
                        messages.Add(new { role = msg.Role, content = msg.Content });
                    }
                }

                // Build the current user message, optionally with scene context
                string fullMessage = userMessage;
                if (!string.IsNullOrEmpty(sceneContext))
                {
                    fullMessage = string.Format("[SCENE CONTEXT]\n{0}\n[END SCENE CONTEXT]\n\n{1}",
                        sceneContext, userMessage);
                }

                messages.Add(new { role = "user", content = fullMessage });

                // Build the request body
                var requestBody = new
                {
                    model = DefaultModel,
                    max_tokens = DefaultMaxTokens,
                    system = SystemPrompt,
                    messages = messages
                };

                string jsonBody = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", ApiVersion);

                // Send the request
                var response = await _httpClient.SendAsync(request, cancellationToken);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return string.Format("API Error ({0}): {1}", response.StatusCode, responseBody);
                }

                // Parse the response
                var apiResponse = JsonSerializer.Deserialize<ApiResponse>(responseBody);

                if (apiResponse?.Content != null && apiResponse.Content.Count > 0)
                {
                    // Concatenate all text blocks in the response
                    var sb = new StringBuilder();
                    foreach (var block in apiResponse.Content)
                    {
                        if (block.Type == "text")
                            sb.AppendLine(block.Text);
                    }
                    return sb.ToString().Trim();
                }

                return "Error: Received empty response from Claude.";
            }
            catch (HttpRequestException ex)
            {
                return string.Format("Network error: {0}", ex.Message);
            }
            catch (TaskCanceledException)
            {
                return "Error: Request timed out.";
            }
            catch (Exception ex)
            {
                return string.Format("Error: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Build the full system prompt including tagging schema awareness.
        /// </summary>
        private static string BuildSystemPrompt()
        {
            var sb = new StringBuilder();

            sb.AppendLine(@"You are Claude, an AI assistant integrated into Rhinoceros 3D (Rhino 8) — a professional 3D modeling application used by architects, designers, and engineers.

You can help the user by:
1. Answering questions about Rhino, RhinoCommon, RhinoScript, and Grasshopper.
2. Generating Python scripts that run inside Rhino's Python editor (rhinoscriptsyntax and RhinoCommon).
3. Analyzing geometry and scene context provided to you.
4. Tagging geometry with semantic metadata for construction documentation.
5. Querying and reasoning about tagged building elements.

CRITICAL rules when generating Rhino Python scripts:
- Scripts run in Rhino 8's IronPython 2.7 engine. You MUST use Python 2 syntax only.
- Do NOT use f-strings. Use .format() or % formatting instead.
- Do NOT use 'print(f""...)'. Use 'print(""..."".format(...))' or 'print(""..."" % (...))'.
- ALWAYS start scripts with these imports and document setup:
    import rhinoscriptsyntax as rs
    import scriptcontext as sc
    import Rhino
    sc.doc = Rhino.RhinoDoc.ActiveDoc
  This ensures geometry is created in the active Rhino document.
- At the end of the script, call sc.doc.Views.Redraw() to refresh viewports.
- rs.AddBox() requires a list of 8 corner points (Point3d or [x,y,z] tuples), NOT a corner + dimensions.
- For simple boxes, prefer using RhinoCommon directly:
    import Rhino.Geometry as rg
    box = rg.Box(rg.Plane.WorldXY, rg.Interval(-50,50), rg.Interval(-50,50), rg.Interval(-5,5))
    sc.doc.Objects.AddBrep(box.ToBrep())
- Brep.CreateBooleanDifference/Union/Intersection require CLOSED (solid) Breps. Open surfaces from CreateFromLoft will fail. To make a solid wall, cap the ends or use CreateFromLoft with closed curves and cap planar holes.
- For complex models, prefer creating individual geometric primitives (cylinders, boxes, lofts) and adding them directly to the document rather than complex boolean operations which are fragile.
- Always include comments explaining what the script does.
- Wrap code in a main() function and call it directly (do NOT use 'if __name__' guard).
- Output results via print() statements. Avoid rs.MessageBox() unless explicitly requested.

When scene context is provided, use it to give specific, relevant advice about the user's current model.");

            sb.AppendLine();
            sb.AppendLine("SEMANTIC TAGGING SYSTEM:");
            sb.AppendLine("RhinoClaude uses a semantic tagging system that stores architectural metadata on Rhino objects via User Text.");
            sb.AppendLine("Tags use the 'RC:' namespace prefix. When working with tags in scripts, read/write them with:");
            sb.AppendLine("  rs.SetUserText(obj_id, 'RC:ElementType', 'Wall')");
            sb.AppendLine("  value = rs.GetUserText(obj_id, 'RC:ElementType')");
            sb.AppendLine();
            sb.Append(TagSchema.GetSchemaDescription());
            sb.AppendLine();
            sb.AppendLine("When scene context includes tag data, use it to reason about the building:");
            sb.AppendLine("- Identify fire-rated assemblies and their implications.");
            sb.AppendLine("- Understand spatial relationships between tagged elements.");
            sb.AppendLine("- Detect potential conflicts (e.g., non-rated door in rated wall).");
            sb.AppendLine("- Suggest tagging for untagged objects based on their geometry and context.");

            return sb.ToString();
        }

        #region API Response Models

        private class ApiResponse
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }

            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("role")]
            public string Role { get; set; }

            [JsonPropertyName("content")]
            public List<ContentBlock> Content { get; set; }

            [JsonPropertyName("model")]
            public string Model { get; set; }

            [JsonPropertyName("stop_reason")]
            public string StopReason { get; set; }

            [JsonPropertyName("usage")]
            public Usage Usage { get; set; }
        }

        private class ContentBlock
        {
            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("text")]
            public string Text { get; set; }
        }

        private class Usage
        {
            [JsonPropertyName("input_tokens")]
            public int InputTokens { get; set; }

            [JsonPropertyName("output_tokens")]
            public int OutputTokens { get; set; }
        }

        #endregion
    }

    /// <summary>Simple model for tracking conversation history.</summary>
    public class ConversationMessage
    {
        public string Role { get; set; }  // "user" or "assistant"
        public string Content { get; set; }

        public ConversationMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }
}
