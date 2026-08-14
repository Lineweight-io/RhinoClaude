using System;
using System.Text;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Suggested filenames for the two sidebar exports. Kept free of RhinoCommon so the
    /// naming rule — including what happens to an unsaved document or a document whose name
    /// contains characters Windows will not accept — is unit-testable.
    /// </summary>
    public static class ExportNaming
    {
        /// <summary>Used when the document has never been saved and so has no name.</summary>
        public const string UntitledDocument = "Untitled";

        public const string ConversationPrefix = "RhinoClaude_conversation";
        public const string ResultPrefix = "RhinoClaude_result";

        /// <summary>
        /// <c>RhinoClaude_conversation_{docname}_{yyyyMMdd_HHmm}.md</c>. The timestamp is local:
        /// the file is named for the human who will look for it, not for UTC.
        /// </summary>
        public static string ConversationFileName(string documentName, DateTime localTime) =>
            Compose(ConversationPrefix, documentName, localTime, ".md");

        /// <summary><c>RhinoClaude_result_{docname}_{yyyyMMdd_HHmm}.3dm</c>.</summary>
        public static string ResultFileName(string documentName, DateTime localTime) =>
            Compose(ResultPrefix, documentName, localTime, ".3dm");

        private static string Compose(string prefix, string documentName, DateTime localTime, string extension) =>
            prefix + "_" + SanitizeDocumentName(documentName) + "_" +
            localTime.ToString("yyyyMMdd_HHmm", System.Globalization.CultureInfo.InvariantCulture) +
            extension;

        /// <summary>
        /// Reduce a document name to something safe to paste into a filename: drop the
        /// directory and the .3dm extension, replace anything Windows rejects, and collapse
        /// whitespace to underscores so the result survives being pasted into a chat window.
        /// </summary>
        public static string SanitizeDocumentName(string documentName)
        {
            if (string.IsNullOrWhiteSpace(documentName)) return UntitledDocument;

            string name = documentName.Trim();

            // RhinoDoc.Name is usually bare, but RhinoDoc.Path is a full path — accept either.
            int slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0 && slash < name.Length - 1) name = name.Substring(slash + 1);

            if (name.EndsWith(".3dm", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c)) { sb.Append('_'); continue; }
                // Windows-invalid characters plus the ones that make a name awkward to share.
                if ("\\/:*?\"<>|,;".IndexOf(c) >= 0 || char.IsControl(c)) { sb.Append('_'); continue; }
                sb.Append(c);
            }

            // Collapse runs of underscores so "Restroom  Test" does not become "Restroom__Test".
            var collapsed = new StringBuilder(sb.Length);
            bool previousUnderscore = false;
            foreach (char c in sb.ToString())
            {
                if (c == '_')
                {
                    if (previousUnderscore) continue;
                    previousUnderscore = true;
                }
                else previousUnderscore = false;
                collapsed.Append(c);
            }

            string result = collapsed.ToString().Trim('_', '.');

            // A name that was entirely punctuation, or long enough to threaten MAX_PATH once
            // the prefix and timestamp are added, is not worth preserving verbatim.
            if (result.Length == 0) return UntitledDocument;
            return result.Length <= 60 ? result : result.Substring(0, 60).TrimEnd('_', '.');
        }
    }
}
