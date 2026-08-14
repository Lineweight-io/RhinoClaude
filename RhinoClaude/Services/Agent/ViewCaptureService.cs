using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoClaude.Agent;

namespace RhinoClaude.Services.Agent
{
    /// <summary>One requested camera position. Exactly one of the four variants is set.</summary>
    public sealed class CameraShot
    {
        public string NamedView { get; set; }

        public Point3d? CameraLocation { get; set; }
        public Point3d? CameraTarget { get; set; }
        public Vector3d? CameraUp { get; set; }

        public string Preset { get; set; }

        public double? OrbitYawDegrees { get; set; }
        public double? OrbitPitchDegrees { get; set; }

        /// <summary>Null or empty means frame the whole document; otherwise frame these ids.</summary>
        public List<Guid> FramingIds { get; set; }

        public string Describe()
        {
            if (!string.IsNullOrEmpty(NamedView)) return "namedView:" + NamedView;
            if (!string.IsNullOrEmpty(Preset)) return "preset:" + Preset;
            if (OrbitYawDegrees.HasValue || OrbitPitchDegrees.HasValue)
                return string.Format("orbit:{0}/{1}", OrbitYawDegrees ?? 0, OrbitPitchDegrees ?? 0);
            if (CameraLocation.HasValue) return "explicitPose";
            return "current";
        }
    }

    public sealed class CaptureRequest
    {
        public List<CameraShot> Shots { get; } = new List<CameraShot>();
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 800;
        public string DisplayMode { get; set; } = "shaded";
        public bool ShowGrid { get; set; }
    }

    public sealed class CaptureResult
    {
        public int Index { get; set; }
        public string Shot { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Bytes { get; set; }
        public string MediaType { get; set; } = "image/png";
        public string Base64 { get; set; }
        public byte[] Data { get; set; }
        public double[] CameraLocation { get; set; }
        public double[] CameraTarget { get; set; }
        public string Notes { get; set; }
        public string CachedPath { get; set; }
    }

    /// <summary>
    /// The 3D camera controller from plan §6: the agent supplies camera specs and gets
    /// back one image per shot in a single call, rather than "screenshot the viewport".
    ///
    /// The active viewport's projection is saved before the first shot and restored after
    /// the last, so orbiting the model for the agent's benefit does not disturb the user's view.
    /// UI-thread only.
    /// </summary>
    public sealed class ViewCaptureService
    {
        public const int MaxShotsPerCall = 6;
        private const int MaxDimension = 1600;
        private const int MaxHeight = 1200;
        private const int PngSizeCapBytes = 750 * 1024;

        private readonly RhinoQueryService _query;
        private readonly JsonlLogger _log;
        private readonly Guid _sessionId;

        public ViewCaptureService(RhinoQueryService query, JsonlLogger captureLog, Guid sessionId)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _log = captureLog;
            _sessionId = sessionId;
        }

        /// <summary>Set by the session so log lines can be correlated with the turn that caused them.</summary>
        public string CurrentUserPrompt { get; set; }
        public int CurrentIteration { get; set; }

        public List<CaptureResult> Capture(CaptureRequest request)
        {
            if (request == null || request.Shots.Count == 0)
                throw new ArgumentException("At least one shot is required.");

            if (request.Shots.Count > MaxShotsPerCall)
                throw new ArgumentException(string.Format(
                    "{0} shots requested but the per-call cap is {1}. Split the request into multiple calls.",
                    request.Shots.Count, MaxShotsPerCall));

            var doc = _query.Doc;
            var view = doc.Views.ActiveView;
            if (view == null)
                throw new InvalidOperationException("There is no active Rhino view to capture from.");

            int width = Clamp(request.Width, 160, MaxDimension);
            int height = Clamp(request.Height, 120, MaxHeight);

            var viewport = view.ActiveViewport;
            var savedProjection = new ViewportInfo(viewport);
            var savedDisplayMode = viewport.DisplayMode;

            var results = new List<CaptureResult>();

            try
            {
                var displayMode = ResolveDisplayMode(request.DisplayMode);
                if (displayMode != null)
                {
                    try { viewport.DisplayMode = displayMode; }
                    catch (Exception) { /* stick with the current mode */ }
                }

                for (int i = 0; i < request.Shots.Count; i++)
                {
                    var shot = request.Shots[i];
                    string notes = ApplyShot(doc, viewport, shot);
                    view.Redraw();

                    var result = CaptureOne(view, width, height, request.ShowGrid);
                    result.Index = i;
                    result.Shot = shot.Describe();
                    result.Notes = notes;
                    result.CameraLocation = RhinoQueryService.Pt(viewport.CameraLocation);
                    result.CameraTarget = RhinoQueryService.Pt(viewport.CameraTarget);
                    result.CachedPath = CacheToDisk(result);
                    results.Add(result);
                }
            }
            finally
            {
                // Restore the user's view no matter what went wrong mid-sequence.
                try { viewport.SetViewProjection(savedProjection, true); } catch (Exception) { }
                try { if (savedDisplayMode != null) viewport.DisplayMode = savedDisplayMode; } catch (Exception) { }
                try { view.Redraw(); } catch (Exception) { }
            }

            LogCapture(request, results);
            return results;
        }

        // ── camera positioning ────────────────────────────────────────

        private string ApplyShot(RhinoDoc doc, RhinoViewport viewport, CameraShot shot)
        {
            if (!string.IsNullOrEmpty(shot.NamedView))
                return ApplyNamedView(doc, viewport, shot.NamedView);

            if (shot.CameraLocation.HasValue && shot.CameraTarget.HasValue)
            {
                viewport.SetCameraLocations(shot.CameraTarget.Value, shot.CameraLocation.Value);
                viewport.CameraUp = shot.CameraUp ?? Vector3d.ZAxis;
                return null;
            }

            var framing = ResolveFraming(shot);

            if (!string.IsNullOrEmpty(shot.Preset))
                return ApplyPreset(viewport, shot.Preset, framing);

            if (shot.OrbitYawDegrees.HasValue || shot.OrbitPitchDegrees.HasValue)
                return ApplyOrbit(viewport, shot.OrbitYawDegrees ?? 0, shot.OrbitPitchDegrees ?? 0, framing);

            // No variant set — capture whatever the viewport is showing.
            return "No camera spec supplied; captured the current view unchanged.";
        }

        private string ApplyNamedView(RhinoDoc doc, RhinoViewport viewport, string name)
        {
            int index = doc.NamedViews.FindByName(name);
            if (index < 0)
            {
                var available = Enumerable.Range(0, doc.NamedViews.Count)
                                          .Select(i => doc.NamedViews[i].Name)
                                          .ToList();
                throw new ArgumentException("No named view '" + name + "'. Available: " +
                    (available.Count == 0 ? "(none)" : string.Join(", ", available)));
            }

            var viewInfo = doc.NamedViews[index];
            viewport.SetViewProjection(viewInfo.Viewport, true);
            return null;
        }

        private BoundingBox ResolveFraming(CameraShot shot)
        {
            BoundingBox box = shot.FramingIds != null && shot.FramingIds.Count > 0
                ? _query.BoundingBoxOf(shot.FramingIds)
                : _query.DocumentExtents();

            if (!box.IsValid)
            {
                // Empty document — frame a unit box so the capture is not degenerate.
                box = new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1));
            }

            // Inflate slightly so geometry does not touch the frame edge.
            var diagonal = box.Diagonal.Length;
            double pad = diagonal > RhinoMath.ZeroTolerance ? diagonal * 0.06 : 1.0;
            box.Inflate(pad);
            return box;
        }

        private static string ApplyPreset(RhinoViewport viewport, string preset, BoundingBox framing)
        {
            var center = framing.Center;
            double radius = Math.Max(framing.Diagonal.Length, 1.0);

            Vector3d direction;   // from camera toward target
            Vector3d up = Vector3d.ZAxis;

            switch ((preset ?? string.Empty).ToLowerInvariant())
            {
                case "plan":  direction = -Vector3d.ZAxis; up = Vector3d.YAxis; break;
                case "front": direction =  Vector3d.YAxis; break;
                case "back":  direction = -Vector3d.YAxis; break;
                case "left":  direction =  Vector3d.XAxis; break;
                case "right": direction = -Vector3d.XAxis; break;

                // 30-degree-elevation isometrics from each corner.
                case "iso_ne": direction = IsoDirection(-1, -1); break;
                case "iso_nw": direction = IsoDirection( 1, -1); break;
                case "iso_se": direction = IsoDirection(-1,  1); break;
                case "iso_sw": direction = IsoDirection( 1,  1); break;

                default:
                    throw new ArgumentException("Unknown preset '" + preset +
                        "'. Valid: plan, front, back, left, right, iso_ne, iso_nw, iso_se, iso_sw.");
            }

            var location = center - direction * radius * 1.5;

            // Presets are orthographic — plan, elevation and iso all read better parallel.
            try { viewport.ChangeToParallelProjection(true); } catch (Exception) { }

            viewport.SetCameraLocations(center, location);
            viewport.CameraUp = up;
            viewport.ZoomBoundingBox(framing);
            return null;
        }

        private static Vector3d IsoDirection(double x, double y)
        {
            // 30 degrees above horizontal, looking down at the model.
            const double elevation = 30.0 * Math.PI / 180.0;
            var horizontal = new Vector3d(x, y, 0);
            horizontal.Unitize();
            var direction = new Vector3d(
                horizontal.X * Math.Cos(elevation),
                horizontal.Y * Math.Cos(elevation),
                -Math.Sin(elevation));
            direction.Unitize();
            return direction;
        }

        private static string ApplyOrbit(RhinoViewport viewport, double yawDegrees, double pitchDegrees, BoundingBox framing)
        {
            var center = framing.Center;
            var location = viewport.CameraLocation;
            var offset = location - center;

            if (offset.Length < RhinoMath.ZeroTolerance)
            {
                offset = new Vector3d(0, -Math.Max(framing.Diagonal.Length, 1.0), 0);
            }

            var yaw = Transform.Rotation(RhinoMath.ToRadians(yawDegrees), Vector3d.ZAxis, Point3d.Origin);
            offset.Transform(yaw);

            // Pitch about the axis perpendicular to the (rotated) view direction.
            var right = Vector3d.CrossProduct(offset, Vector3d.ZAxis);
            string notes = null;
            if (right.Length < RhinoMath.ZeroTolerance)
            {
                notes = "Camera was looking straight down; pitch was skipped because the orbit axis is undefined there.";
            }
            else
            {
                right.Unitize();
                var pitch = Transform.Rotation(RhinoMath.ToRadians(pitchDegrees), right, Point3d.Origin);
                offset.Transform(pitch);
            }

            viewport.SetCameraLocations(center, center + offset);
            viewport.CameraUp = Vector3d.ZAxis;
            viewport.ZoomBoundingBox(framing);
            return notes;
        }

        private static DisplayModeDescription ResolveDisplayMode(string name)
        {
            string target;
            switch ((name ?? "shaded").ToLowerInvariant())
            {
                case "wireframe": target = "Wireframe"; break;
                case "rendered": target = "Rendered"; break;
                case "shaded":
                default: target = "Shaded"; break;
            }

            try { return DisplayModeDescription.FindByName(target); }
            catch (Exception) { return null; }
        }

        // ── bitmap encoding ───────────────────────────────────────────

        private static CaptureResult CaptureOne(RhinoView view, int width, int height, bool showGrid)
        {
            var size = new Size(width, height);
            Bitmap bitmap = null;

            try
            {
                bitmap = view.CaptureToBitmap(size, showGrid, false, false);
                if (bitmap == null) bitmap = view.CaptureToBitmap(size);
                if (bitmap == null)
                    throw new InvalidOperationException("Rhino returned no bitmap for the view capture.");

                var result = new CaptureResult { Width = bitmap.Width, Height = bitmap.Height };

                byte[] png = Encode(bitmap, ImageFormat.Png);
                if (png.Length <= PngSizeCapBytes)
                {
                    result.Data = png;
                    result.MediaType = "image/png";
                }
                else
                {
                    // Fall back to JPEG q85 rather than sending a 1 MB PNG per shot.
                    byte[] jpeg = EncodeJpeg(bitmap, 85L);
                    if (jpeg != null && jpeg.Length < png.Length)
                    {
                        result.Data = jpeg;
                        result.MediaType = "image/jpeg";
                        result.Notes = "PNG exceeded the 750 KB cap; re-encoded as JPEG q85.";
                    }
                    else
                    {
                        result.Data = png;
                        result.MediaType = "image/png";
                    }
                }

                result.Bytes = result.Data.Length;
                result.Base64 = Convert.ToBase64String(result.Data);
                return result;
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        private static byte[] Encode(Bitmap bitmap, ImageFormat format)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, format);
                return stream.ToArray();
            }
        }

        private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
        {
            try
            {
                var codec = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                if (codec == null) return null;

                using (var parameters = new EncoderParameters(1))
                using (var stream = new MemoryStream())
                {
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                    bitmap.Save(stream, codec, parameters);
                    return stream.ToArray();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ── side effects ──────────────────────────────────────────────

        private string CacheToDisk(CaptureResult result)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "RhinoClaude", "screenshots", _sessionId.ToString("N"));
                Directory.CreateDirectory(dir);
                string ext = result.MediaType == "image/jpeg" ? ".jpg" : ".png";
                string path = Path.Combine(dir,
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + result.Index + ext);
                File.WriteAllBytes(path, result.Data);
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void LogCapture(CaptureRequest request, List<CaptureResult> results)
        {
            _log?.Append(new Dictionary<string, object>
            {
                { "kind", "capture_views" },
                { "sessionId", _sessionId.ToString() },
                { "iteration", CurrentIteration },
                { "userPrompt", CurrentUserPrompt },
                { "shotCount", request.Shots.Count },
                { "shots", request.Shots.Select(s => s.Describe()).ToList() },
                { "displayMode", request.DisplayMode },
                { "width", request.Width },
                { "height", request.Height },
                { "totalBytes", results.Sum(r => r.Bytes) }
            });
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);

        // ── input parsing ─────────────────────────────────────────────

        /// <summary>Parse the tool's <c>shots</c> array into <see cref="CameraShot"/> values.</summary>
        public static CaptureRequest ParseRequest(JsonElement input)
        {
            var request = new CaptureRequest();

            if (input.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number)
                request.Width = w.GetInt32();
            if (input.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number)
                request.Height = h.GetInt32();
            if (input.TryGetProperty("displayMode", out var dm) && dm.ValueKind == JsonValueKind.String)
                request.DisplayMode = dm.GetString();
            if (input.TryGetProperty("showGrid", out var sg))
                request.ShowGrid = sg.ValueKind == JsonValueKind.True;

            if (!input.TryGetProperty("shots", out var shots) || shots.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("'shots' must be an array with at least one camera spec.");

            foreach (var element in shots.EnumerateArray())
            {
                var shot = new CameraShot();

                if (element.TryGetProperty("namedView", out var nv) && nv.ValueKind == JsonValueKind.String)
                    shot.NamedView = nv.GetString();

                if (element.TryGetProperty("cameraLocation", out var cl) && cl.ValueKind == JsonValueKind.Array)
                    shot.CameraLocation = RhinoQueryService.ReadPoint(cl);
                if (element.TryGetProperty("cameraTarget", out var ct) && ct.ValueKind == JsonValueKind.Array)
                    shot.CameraTarget = RhinoQueryService.ReadPoint(ct);
                if (element.TryGetProperty("cameraUp", out var cu) && cu.ValueKind == JsonValueKind.Array)
                    shot.CameraUp = RhinoQueryService.ReadVector(cu);

                if (element.TryGetProperty("preset", out var pr) && pr.ValueKind == JsonValueKind.String)
                    shot.Preset = pr.GetString();

                if (element.TryGetProperty("orbitFromCurrent", out var orbit) && orbit.ValueKind == JsonValueKind.Object)
                {
                    if (orbit.TryGetProperty("yawDegrees", out var yaw) && yaw.ValueKind == JsonValueKind.Number)
                        shot.OrbitYawDegrees = yaw.GetDouble();
                    if (orbit.TryGetProperty("pitchDegrees", out var pitch) && pitch.ValueKind == JsonValueKind.Number)
                        shot.OrbitPitchDegrees = pitch.GetDouble();
                    if (orbit.TryGetProperty("framingIds", out var oids))
                        shot.FramingIds = ReadIds(oids);
                }

                if (element.TryGetProperty("framingIds", out var fids))
                    shot.FramingIds = ReadIds(fids);

                if (shot.CameraLocation.HasValue != shot.CameraTarget.HasValue)
                    throw new ArgumentException("cameraLocation and cameraTarget must be supplied together.");

                request.Shots.Add(shot);
            }

            return request;
        }

        private static List<Guid> ReadIds(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array) return null;
            var ids = new List<Guid>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id))
                    ids.Add(id);
            }
            return ids;
        }
    }
}
