using Newtonsoft.Json;
using Omnipotent.Services.OmniTumblr.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniTumblr.Api
{
    /// <summary>A file to send alongside an NPF post, referenced from a content block by <see cref="Identifier"/>.</summary>
    public sealed class NpfUpload
    {
        public string Identifier { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string MimeType { get; set; } = "application/octet-stream";
        public string FileName { get; set; } = "media";
    }

    /// <summary>The body of POST /v2/blog/{blog}/posts (Neue Post Format).</summary>
    public sealed class NpfPostRequest
    {
        public List<Dictionary<string, object?>> Content { get; set; } = new();
        public List<Dictionary<string, object?>>? Layout { get; set; }
        public string State { get; set; } = "published";
        /// <summary>Comma-separated, as Tumblr expects.</summary>
        public string? Tags { get; set; }
        public string? SourceUrl { get; set; }
        public string? Slug { get; set; }
        public List<NpfUpload> Uploads { get; set; } = new();

        public string ToJson()
        {
            var body = new Dictionary<string, object?>
            {
                ["content"] = Content,
                ["state"] = State,
            };
            if (Layout is { Count: > 0 }) body["layout"] = Layout;
            if (!string.IsNullOrWhiteSpace(Tags)) body["tags"] = Tags;
            if (!string.IsNullOrWhiteSpace(SourceUrl)) body["source_url"] = SourceUrl;
            if (!string.IsNullOrWhiteSpace(Slug)) body["slug"] = Slug;
            return JsonConvert.SerializeObject(body, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }
    }

    public sealed class NpfBuildInput
    {
        public PostKind Kind { get; set; }
        public IReadOnlyList<PostMedia> Media { get; set; } = Array.Empty<PostMedia>();
        public string? Caption { get; set; }
        public string? Title { get; set; }
        public string? LinkUrl { get; set; }
        public IEnumerable<string> Tags { get; set; } = Array.Empty<string>();
        public string? SourceUrl { get; set; }
        public string? Slug { get; set; }
        public TumblrPostState State { get; set; } = TumblrPostState.Published;
        public string? AltText { get; set; }
    }

    /// <summary>
    /// Builds Neue Post Format requests. Limits enforced here (from Tumblr's NPF spec): one native video
    /// per post, at most 30 image blocks, text blocks of at most 4,096 code points, ≤30 tags.
    /// </summary>
    public static class TumblrNpf
    {
        public const int MaxTextBlockCodePoints = 4096;
        public const int MaxImages = 30;
        public const int MaxTags = 30;
        public const int MaxTagLength = 140;

        public static NpfPostRequest Build(NpfBuildInput input)
        {
            var request = new NpfPostRequest
            {
                State = input.State switch
                {
                    TumblrPostState.Draft => "draft",
                    TumblrPostState.Private => "private",
                    _ => "published",
                },
                Tags = NormalizeTagsCsv(input.Tags),
                SourceUrl = string.IsNullOrWhiteSpace(input.SourceUrl) ? null : input.SourceUrl.Trim(),
                Slug = string.IsNullOrWhiteSpace(input.Slug) ? null : input.Slug,
            };

            switch (input.Kind)
            {
                case PostKind.Video:
                {
                    var video = input.Media.FirstOrDefault(m => m.IsVideo)
                        ?? throw new InvalidOperationException("A video post needs a video file.");
                    var upload = AddUpload(request, video, "video0");
                    var media = new Dictionary<string, object?>
                    {
                        ["type"] = video.MimeType,
                        ["identifier"] = upload.Identifier,
                    };
                    if (video.Width is > 0 && video.Height is > 0)
                    {
                        media["width"] = video.Width;
                        media["height"] = video.Height;
                    }
                    var block = new Dictionary<string, object?>
                    {
                        ["type"] = "video",
                        ["provider"] = "tumblr",
                        ["media"] = media,
                    };
                    if (video.DurationSeconds is > 0)
                        block["duration"] = (long)Math.Round(video.DurationSeconds.Value * 1000);
                    request.Content.Add(block);
                    break;
                }
                case PostKind.Photo:
                {
                    var images = input.Media.Where(m => !m.IsVideo).Take(MaxImages).ToList();
                    if (images.Count == 0) throw new InvalidOperationException("A photo post needs at least one image.");
                    for (int i = 0; i < images.Count; i++)
                    {
                        var upload = AddUpload(request, images[i], "image" + i);
                        var media = new Dictionary<string, object?>
                        {
                            ["type"] = images[i].MimeType,
                            ["identifier"] = upload.Identifier,
                        };
                        if (images[i].Width is > 0 && images[i].Height is > 0)
                        {
                            media["width"] = images[i].Width;
                            media["height"] = images[i].Height;
                        }
                        var block = new Dictionary<string, object?>
                        {
                            ["type"] = "image",
                            ["media"] = new List<object> { media },
                        };
                        if (!string.IsNullOrWhiteSpace(input.AltText)) block["alt_text"] = Truncate(input.AltText.Trim(), 1000);
                        request.Content.Add(block);
                    }
                    break;
                }
                case PostKind.Link:
                {
                    if (string.IsNullOrWhiteSpace(input.LinkUrl)) throw new InvalidOperationException("A link post needs a URL.");
                    var block = new Dictionary<string, object?> { ["type"] = "link", ["url"] = input.LinkUrl.Trim() };
                    if (!string.IsNullOrWhiteSpace(input.Title)) block["title"] = Truncate(input.Title.Trim(), 140);
                    request.Content.Add(block);
                    break;
                }
                case PostKind.Text:
                {
                    if (!string.IsNullOrWhiteSpace(input.Title))
                        request.Content.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["subtype"] = "heading1",
                            ["text"] = TruncateCodePoints(input.Title.Trim(), MaxTextBlockCodePoints),
                        });
                    break;
                }
            }

            foreach (string paragraph in SplitText(input.Caption))
                request.Content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = paragraph });

            if (request.Content.Count == 0)
                throw new InvalidOperationException("The post has no content.");
            return request;
        }

        private static NpfUpload AddUpload(NpfPostRequest request, PostMedia media, string identifier)
        {
            var upload = new NpfUpload
            {
                Identifier = identifier,
                FilePath = media.Path,
                MimeType = media.MimeType,
                FileName = SafeFileName(Path.GetFileName(media.Path), identifier),
            };
            request.Uploads.Add(upload);
            return upload;
        }

        /// <summary>One text block per paragraph (blank lines dropped), each within Tumblr's 4,096 code point limit.</summary>
        public static List<string> SplitText(string? text)
        {
            var blocks = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return blocks;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string paragraph = raw.Trim();
                if (paragraph.Length == 0) continue;
                var runes = paragraph.EnumerateRunes().ToList();
                for (int start = 0; start < runes.Count; start += MaxTextBlockCodePoints)
                {
                    var sb = new StringBuilder();
                    foreach (var rune in runes.Skip(start).Take(MaxTextBlockCodePoints)) sb.Append(rune.ToString());
                    blocks.Add(sb.ToString());
                }
            }
            return blocks;
        }

        /// <summary>Tumblr tags: no '#', no commas, ≤140 chars each, ≤30 per post, case-insensitively unique.</summary>
        public static List<string> NormalizeTags(IEnumerable<string>? tags, int max = MaxTags)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? raw in tags ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string tag = Regex.Replace(raw.Replace(",", " ").Trim().TrimStart('#').Trim(), "\\s+", " ");
                if (tag.Length == 0) continue;
                if (tag.Length > MaxTagLength) tag = tag[..MaxTagLength].Trim();
                if (seen.Add(tag)) result.Add(tag);
                if (result.Count >= Math.Min(max, MaxTags)) break;
            }
            return result;
        }

        public static string? NormalizeTagsCsv(IEnumerable<string>? tags)
        {
            var list = NormalizeTags(tags);
            return list.Count == 0 ? null : string.Join(",", list);
        }

        /// <summary>
        /// A unique, readable permalink slug: a few words of the caption plus part of the post id. The id
        /// part makes it unique, so after an ambiguous failure the post can be found again by slug.
        /// </summary>
        public static string MakeSlug(string? caption, string postId)
        {
            string id = new string(postId.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (id.Length > 8) id = id[..8];
            var words = Regex.Matches((caption ?? string.Empty).ToLowerInvariant(), "[a-z0-9]+")
                .Select(m => m.Value)
                .Take(6)
                .ToList();
            string head = string.Join("-", words);
            if (head.Length > 40) head = head[..40].TrimEnd('-');
            return string.IsNullOrEmpty(head) ? "post-" + id : head + "-" + id;
        }

        /// <summary>
        /// The HTTP body: plain JSON when there is nothing to upload, otherwise multipart/form-data whose
        /// first part ("json") carries the post and whose other parts are the media, named by identifier.
        /// </summary>
        public static HttpContent CreateHttpContent(NpfPostRequest request)
        {
            string json = request.ToJson();
            if (request.Uploads.Count == 0)
                return new StringContent(json, Encoding.UTF8, "application/json");

            var multipart = new MultipartFormDataContent("TumblrBoundary" + Guid.NewGuid().ToString("N"));
            var jsonPart = new StringContent(json, Encoding.UTF8, "application/json");
            jsonPart.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"json\"" };
            multipart.Add(jsonPart);

            foreach (var upload in request.Uploads)
            {
                var stream = new FileStream(upload.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var filePart = new StreamContent(stream, 81920);
                filePart.Headers.ContentType = new MediaTypeHeaderValue(upload.MimeType);
                filePart.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
                {
                    Name = $"\"{upload.Identifier}\"",
                    FileName = $"\"{upload.FileName}\"",
                };
                multipart.Add(filePart);
            }
            return multipart;
        }

        private static string SafeFileName(string? name, string fallback)
        {
            string ext = Path.GetExtension(name ?? string.Empty);
            string stem = new string(Path.GetFileNameWithoutExtension(name ?? string.Empty)
                .Where(c => c < 128 && (char.IsLetterOrDigit(c) || c is '-' or '_' or '.')).ToArray());
            if (stem.Length == 0) stem = fallback;
            if (stem.Length > 60) stem = stem[..60];
            string safeExt = new string(ext.Where(c => c < 128 && (char.IsLetterOrDigit(c) || c == '.')).ToArray());
            return stem + safeExt;
        }

        private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

        private static string TruncateCodePoints(string value, int max)
        {
            var runes = value.EnumerateRunes().Take(max);
            var sb = new StringBuilder();
            foreach (var rune in runes) sb.Append(rune.ToString());
            return sb.ToString();
        }
    }
}
